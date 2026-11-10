using System.Net;
using Jellyfin.Plugin.Terebi.Catalog;
using Jellyfin.Plugin.Terebi.Health;
using Jellyfin.Plugin.Terebi.Sources;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Streaming;

/// <summary>
/// One live stream handed to Jellyfin for a logical channel. It pulls bytes from the channel's streams in
/// order (TS directly, HLS by walking the media playlist and downloading segments) into a ring buffer that
/// every viewer of the channel reads from. When the current source fails to start, stalls or ends, the next
/// one is opened without closing the buffer, so the player sees a hiccup instead of an error.
/// </summary>
public sealed class FailoverLiveStream : ILiveStream, IDirectStreamProvider, IDisposable
{
    public const string TunerHostId = "Terebi";

    private readonly IReadOnlyList<(RawStream Stream, SourceDefinition Source)> _candidates;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HealthChecker _health;
    private readonly ILogger _logger;
    private readonly RingBuffer _buffer;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;
    private volatile bool _alive = true;
    private int _currentIndex = -1;

    public FailoverLiveStream(
        Guid channelId,
        string channelName,
        IReadOnlyList<(RawStream Stream, SourceDefinition Source)> candidates,
        IServerApplicationHost appHost,
        IHttpClientFactory httpClientFactory,
        HealthChecker health,
        ILogger logger)
    {
        _candidates = candidates;
        _httpClientFactory = httpClientFactory;
        _health = health;
        _logger = logger;
        ChannelId = channelId;
        ChannelName = channelName;
        _buffer = new RingBuffer(Math.Max(4, Plugin.Instance?.Configuration.StreamBufferMiB ?? 16) * 1024 * 1024);
        UniqueId = Guid.NewGuid().ToString("N");
        OriginalStreamId = channelId.ToString("N");
        var path = $"/LiveTv/LiveStreamFiles/{UniqueId}/stream.ts";
        MediaSource = new MediaSourceInfo
        {
            Id = OriginalStreamId,
            Name = channelName,
            Path = appHost.GetApiUrlForLocalAccess() + path,
            EncoderPath = appHost.GetApiUrlForLocalAccess() + path,
            Protocol = MediaProtocol.Http,
            EncoderProtocol = MediaProtocol.Http,
            Container = "ts",
            IsInfiniteStream = true,
            IsRemote = true,
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = true,
            IgnoreDts = true,
            IgnoreIndex = true,
            ReadAtNativeFramerate = false,
            AnalyzeDurationMs = 3000,
            MediaStreams = [],
        };
    }

    public Guid ChannelId { get; }

    public string ChannelName { get; }

    public int ConsumerCount { get; set; }

    public string OriginalStreamId { get; set; }

    string ILiveStream.TunerHostId => TunerHostId;

    public bool EnableStreamSharing => true;

    public MediaSourceInfo MediaSource { get; set; }

    public string UniqueId { get; }

    /// <summary>Gets the source id currently being pulled from, or null.</summary>
    public string? ActiveSourceId => _currentIndex >= 0 && _currentIndex < _candidates.Count ? _candidates[_currentIndex].Source.Id : null;

    public string? ActiveStreamRef => _currentIndex >= 0 && _currentIndex < _candidates.Count ? _candidates[_currentIndex].Stream.Ref : null;

    public int Switches { get; private set; }

    public DateTimeOffset OpenedUtc { get; } = DateTimeOffset.UtcNow;

    public long BytesWritten => _buffer.Written;

    public Task Open(CancellationToken openCancellationToken)
    {
        if (_pump is not null)
        {
            return Task.CompletedTask;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pump = Task.Run(() => PumpAsync(ready, _cts.Token), CancellationToken.None);
        // Give the player data as soon as the first source produced bytes, or fail if none did.
        return ready.Task.WaitAsync(openCancellationToken);
    }

    public async Task Close()
    {
        _alive = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _buffer.Complete();
        _logger.LogInformation("Terebi: closed stream for {Channel} after {Bytes} bytes, {Switches} source switches", ChannelName, _buffer.Written, Switches);
    }

    public Stream GetStream() => new RingBufferReadStream(_buffer, () => _alive && !_buffer.Completed, () => Volatile.Write(ref _lastReadTicks, DateTimeOffset.UtcNow.UtcTicks));

    private long _lastReadTicks = DateTimeOffset.UtcNow.UtcTicks;

    /// <summary>Gets when a consumer last took bytes out of the buffer (start time until the first read).</summary>
    public DateTimeOffset LastReadUtc => new(Volatile.Read(ref _lastReadTicks), TimeSpan.Zero);

    /// <summary>Gets or sets how many consumers came from outside Jellyfin (external players on the raw endpoint); those have no session to check.</summary>
    public int ExternalConsumers { get; set; }

    private async Task PumpAsync(TaskCompletionSource ready, CancellationToken ct)
    {
        var startTimeout = TimeSpan.FromSeconds(Math.Max(3, Plugin.Instance?.Configuration.StreamStartTimeoutSeconds ?? 12));
        var stallTimeout = TimeSpan.FromSeconds(Math.Max(5, Plugin.Instance?.Configuration.StreamStallTimeoutSeconds ?? 20));
        var lastError = "no streams";
        var index = 0;
        var failedInRow = 0;   // candidates in a row that produced nothing; two full rounds and we give up
        var repeats = 0;       // identical finite chunks in a row from the current candidate
        (long Len, ulong Hash)? lastChunk = null;
        string? previousRef = null;
        while (!ct.IsCancellationRequested && failedInRow < _candidates.Count * 2)
        {
            _currentIndex = index % _candidates.Count;
            var (raw, source) = _candidates[_currentIndex];
            if (previousRef is not null && !string.Equals(previousRef, raw.Ref, StringComparison.Ordinal))
            {
                Switches++;
                _logger.LogWarning("Terebi: {Channel}: switching to source '{Source}' ({Ref}) after: {Reason}", ChannelName, source.Name, raw.Ref, lastError);
            }
            else if (previousRef is not null)
            {
                _logger.LogInformation("Terebi: {Channel}: reconnecting {Ref} after: {Reason} (last chunk {Len} bytes, hash {Hash:x}, repeats {Repeats})", ChannelName, raw.Ref, lastError, lastChunk?.Len ?? 0, lastChunk?.Hash ?? 0, repeats);
            }

            previousRef = raw.Ref;
            try
            {
                // Xtream panels answer for an offline channel with a looping "black.ts"; the HLS variant of the
                // same stream names the clip. One tiny request before opening the TS avoids minutes of black.
                if (!raw.IsHls && source.Type == SourceType.Xtream && PlaceholderProbe.TryHlsVariant(raw.Url, out var hlsVariant))
                {
                    var ua = source.UserAgent ?? Plugin.Instance?.Configuration.UserAgent ?? "Mozilla/5.0";
                    using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    checkCts.CancelAfter(TimeSpan.FromSeconds(5));
                    var reason = await PlaceholderProbe.CheckAsync(_httpClientFactory.CreateClient("Terebi"), ua, hlsVariant, checkCts.Token).ConfigureAwait(false);
                    if (reason is not null)
                    {
                        throw new IOException(reason);
                    }
                }

                var (produced, hash) = raw.IsHls
                    ? (await PullHlsAsync(raw, source, ready, startTimeout, stallTimeout, ct).ConfigureAwait(false), 0UL)
                    : await PullTsAsync(raw, source, ready, startTimeout, stallTimeout, ct).ConfigureAwait(false);
                if (produced > 0)
                {
                    _health.Report(raw.Ref, true);
                    failedInRow = 0;
                    lastError = "stream ended";

                    // The provider closed after a finite chunk. A chunked live server hands out the next piece on
                    // reconnect; an offline channel loops the very same clip ("black.ts"), which a player shows as
                    // a black screen forever. Three identical chunks in a row means the latter: mark it and move on.
                    if (!raw.IsHls && lastChunk is { } prev && prev.Len == produced && (prev.Hash == hash || produced < 64 * 1024 * 1024))
                    {
                        repeats++;
                        if (repeats >= 2)
                        {
                            lastError = "provider loops the same clip (offline placeholder)";
                            _health.Report(raw.Ref, false, lastError);
                            lastChunk = null;
                            repeats = 0;
                            index++;
                            failedInRow++;
                            continue;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        repeats = 0;
                    }

                    lastChunk = raw.IsHls ? null : (produced, hash);
                    continue; // same candidate again
                }

                lastError = "no data";
                _health.Report(raw.Ref, false, lastError);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is { } status && PlaceholderProbe.IsBusyStatus((int)status))
            {
                // The account's only slot is taken (another player, a probe). Says nothing about the stream itself.
                lastError = $"provider connection limit reached (HTTP {(int)status})";
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
            {
                lastError = ex.Message;
                _health.Report(raw.Ref, false, ex.Message);
            }

            lastChunk = null;
            repeats = 0;
            index++;
            failedInRow++;
        }

        _alive = false;
        _buffer.Complete();
        ready.TrySetException(new IOException("Terebi: no working stream for " + ChannelName + " (" + lastError + ")"));
    }

    private async Task<(long Produced, ulong Hash)> PullTsAsync(RawStream raw, SourceDefinition source, TaskCompletionSource ready, TimeSpan startTimeout, TimeSpan stallTimeout, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient("Terebi.Stream");
        using var request = new HttpRequestMessage(HttpMethod.Get, raw.Url);
        request.Headers.UserAgent.ParseAdd(source.UserAgent ?? Plugin.Instance?.Configuration.UserAgent ?? "Mozilla/5.0");
        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startCts.CancelAfter(startTimeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, startCts.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.PermanentRedirect)
        {
            throw new HttpRequestException("redirect not followed: " + response.Headers.Location);
        }

        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[188 * 350];
        long total = 0;
        var hash = 14695981039346656037UL; // FNV-1a over everything received, to recognise a repeated clip
        while (!ct.IsCancellationRequested)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(total == 0 ? startTimeout : stallTimeout);
            int n;
            try
            {
                n = await body.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException(total == 0 ? "no data within start timeout" : "stall");
            }

            if (n <= 0)
            {
                break;
            }

            // A playlist where a TS was expected: hand over to the HLS puller.
            if (total == 0 && n >= 7 && buffer[0] == (byte)'#' && buffer[1] == (byte)'E' && buffer[2] == (byte)'X' && buffer[3] == (byte)'T' && buffer[4] == (byte)'M')
            {
                return (await PullHlsAsync(raw, source, ready, startTimeout, stallTimeout, ct).ConfigureAwait(false), 0UL);
            }

            for (var i = 0; i < n; i++)
            {
                hash = (hash ^ buffer[i]) * 1099511628211UL;
            }

            _buffer.Write(buffer.AsSpan(0, n));
            total += n;
            if (total >= 188 * 64)
            {
                ready.TrySetResult();
            }
        }

        return (total, hash);
    }

    /// <summary>Minimal HLS client: follows one variant, downloads new segments as they appear, writes their bytes.</summary>
    private async Task<long> PullHlsAsync(RawStream raw, SourceDefinition source, TaskCompletionSource ready, TimeSpan startTimeout, TimeSpan stallTimeout, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient("Terebi.Stream");
        var ua = source.UserAgent ?? Plugin.Instance?.Configuration.UserAgent ?? "Mozilla/5.0";
        var playlistUri = new Uri(raw.Url);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        var idleSince = DateTimeOffset.UtcNow;
        var targetDuration = 4.0;
        var segmentBuffer = new byte[1 << 16];

        for (var round = 0; !ct.IsCancellationRequested; round++)
        {
            var (text, finalUri) = await GetTextAsync(http, ua, playlistUri, total == 0 ? startTimeout : stallTimeout, ct).ConfigureAwait(false);
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)))
            {
                // Master playlist: pick the highest bandwidth variant.
                var best = (Bandwidth: -1L, Url: (string?)null);
                for (var i = 0; i < lines.Count - 1; i++)
                {
                    if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var bw = 0L;
                    var idx = lines[i].IndexOf("BANDWIDTH=", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var digits = new string(lines[i][(idx + 10)..].TakeWhile(char.IsDigit).ToArray());
                        long.TryParse(digits, out bw);
                    }

                    var url = lines.Skip(i + 1).FirstOrDefault(l => !l.StartsWith('#'));
                    if (url is not null && bw >= best.Bandwidth)
                    {
                        best = (bw, url);
                    }
                }

                if (best.Url is null)
                {
                    throw new IOException("master playlist without variants");
                }

                playlistUri = new Uri(finalUri, best.Url);
                continue;
            }

            if (PlaceholderProbe.Inspect(text) is { } placeholder)
            {
                throw new IOException(placeholder);
            }

            var td = lines.FirstOrDefault(l => l.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal));
            if (td is not null && double.TryParse(td[22..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tdv) && tdv > 0)
            {
                targetDuration = tdv;
            }

            var segments = lines.Where(l => !l.StartsWith('#')).Select(l => new Uri(finalUri, l).ToString()).ToList();
            var fresh = segments.Where(s => !seen.Contains(s)).ToList();
            // On first load start near the live edge (last 3 segments) instead of the whole window.
            if (total == 0 && fresh.Count > 3)
            {
                foreach (var old in fresh.Take(fresh.Count - 3))
                {
                    seen.Add(old);
                }

                fresh = fresh.Skip(fresh.Count - 3).ToList();
            }

            foreach (var seg in fresh)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(seg);
                using var segReq = new HttpRequestMessage(HttpMethod.Get, seg);
                segReq.Headers.UserAgent.ParseAdd(ua);
                using var segCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                segCts.CancelAfter(stallTimeout);
                using var segResp = await http.SendAsync(segReq, HttpCompletionOption.ResponseHeadersRead, segCts.Token).ConfigureAwait(false);
                if (!segResp.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var segStream = await segResp.Content.ReadAsStreamAsync(segCts.Token).ConfigureAwait(false);
                int n;
                var first = true;
                while ((n = await segStream.ReadAsync(segmentBuffer, segCts.Token).ConfigureAwait(false)) > 0)
                {
                    if (first && n >= 4 && segmentBuffer[4] == (byte)'f' && segmentBuffer[5] == (byte)'t' && segmentBuffer[6] == (byte)'y' && segmentBuffer[7] == (byte)'p')
                    {
                        throw new IOException("fMP4 HLS segments are not supported by the restreamer");
                    }

                    first = false;
                    _buffer.Write(segmentBuffer.AsSpan(0, n));
                    total += n;
                }

                idleSince = DateTimeOffset.UtcNow;
                if (total >= 188 * 64)
                {
                    ready.TrySetResult();
                }
            }

            if (lines.Contains("#EXT-X-ENDLIST"))
            {
                return total;
            }

            if (fresh.Count == 0)
            {
                if (DateTimeOffset.UtcNow - idleSince > stallTimeout + TimeSpan.FromSeconds(targetDuration))
                {
                    throw new IOException("HLS playlist stopped advancing");
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(targetDuration / 2, 1, 5)), ct).ConfigureAwait(false);
            }
        }

        return total;
    }

    private static async Task<(string Text, Uri FinalUri)> GetTextAsync(HttpClient http, string ua, Uri uri, TimeSpan timeout, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.UserAgent.ParseAdd(ua);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return (text, resp.RequestMessage?.RequestUri ?? uri);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _buffer.Dispose();
    }
}
