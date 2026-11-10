using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Terebi.Catalog;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Sources;
using Jellyfin.Plugin.Terebi.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Health;

/// <summary>Probe history of one stream.</summary>
public sealed class StreamHealth
{
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("lastOkUtc")]
    public DateTimeOffset? LastOkUtc { get; set; }

    [JsonPropertyName("lastFailUtc")]
    public DateTimeOffset? LastFailUtc { get; set; }

    [JsonPropertyName("lastCheckUtc")]
    public DateTimeOffset? LastCheckUtc { get; set; }

    [JsonPropertyName("consecutiveFailures")]
    public int ConsecutiveFailures { get; set; }

    [JsonPropertyName("latencyMs")]
    public int LatencyMs { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    /// <summary>Gets or sets what the probe saw: "ts", "hls", "hls-empty", "http-404"...</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonIgnore]
    public bool IsDead => ConsecutiveFailures >= Math.Max(1, Plugin.Instance?.Configuration.DeadAfterFailures ?? 2);

    /// <summary>Gets a value indicating whether the stream has never been probed.</summary>
    [JsonIgnore]
    public bool Unknown => LastCheckUtc is null;
}

/// <summary>
/// Probes streams the way a player would start them: HLS playlist plus its first segment, or the first
/// bytes of a transport stream. Results decide which streams are offered and in what order.
/// Per-source concurrency is limited; sources with a single-connection account are probed one at a time
/// and skipped entirely while somebody is watching from that source.
/// </summary>
public sealed class HealthChecker
{
    private readonly LineupStore _store;
    private readonly CatalogStore _catalog;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HealthChecker> _logger;
    private readonly string _path;
    private readonly ConcurrentDictionary<string, StreamHealth> _health;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public HealthChecker(TerebiPaths paths, LineupStore store, CatalogStore catalog, IHttpClientFactory httpClientFactory, ILogger<HealthChecker> logger)
    {
        _store = store;
        _catalog = catalog;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _path = paths.File("health.json");
        var loaded = JsonFile.Load(_path, () => new List<StreamHealth>(), logger);
        _health = new ConcurrentDictionary<string, StreamHealth>(loaded.Select(h => new KeyValuePair<string, StreamHealth>(h.Ref, h)), StringComparer.Ordinal);
    }

    /// <summary>Gets or sets the guide refresh queue (set by the registrator after construction to avoid a dependency cycle).</summary>
    public LiveTv.GuideRefreshQueue? GuideQueue { get; set; }

    private LiveTv.GuideRefreshQueue? _guide => GuideQueue;

    /// <summary>Channels Jellyfin is offered right now: enabled, with at least one enabled stream that is not dead (when dead channels are hidden).</summary>
    private HashSet<Guid> ServedChannels()
    {
        var hideDead = Plugin.Instance?.Configuration.HideDeadChannels ?? true;
        return _store.Lineup.Channels
            .Where(c => c.Enabled && c.Streams.Any(s => s.Enabled))
            .Where(c => !hideDead || !c.Streams.Where(s => s.Enabled).All(s => Get(s.Ref).IsDead))
            .Select(c => c.Id)
            .ToHashSet();
    }

    /// <summary>Gets a function telling whether a source currently has an open live stream (set by the stream manager).</summary>
    public Func<string, bool> IsSourceBusy { get; set; } = _ => false;

    public StreamHealth Get(string streamRef) => _health.GetOrAdd(streamRef, r => new StreamHealth { Ref = r });

    public IReadOnlyCollection<StreamHealth> All => _health.Values.ToList();

    /// <summary>Records the result of a real playback attempt (counts like a probe).</summary>
    public void Report(string streamRef, bool ok, string? error = null, string? kind = null)
    {
        var h = Get(streamRef);
        Apply(h, ok, 0, error, kind);
        ScheduleSave();
    }

    private int _savePending;

    /// <summary>Persists playback verdicts a few seconds later, so a restart does not forget a dead stream.</summary>
    private void ScheduleSave()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Interlocked.Exchange(ref _savePending, 0);
            try
            {
                await SaveAsync().ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Terebi: could not save health");
            }
        });
    }

    public bool IsUsable(string streamRef)
    {
        var h = _health.TryGetValue(streamRef, out var v) ? v : null;
        return h is null || h.Unknown || !h.IsDead;
    }

    /// <summary>Probes every stream referenced by an enabled channel. Sources are probed in parallel, streams within a source with limited concurrency.</summary>
    public async Task RunAsync(IProgress<double>? progress, CancellationToken cancellationToken, bool onlyUnknownOrDead = false, string? onlySourceId = null)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Terebi: health check already running");
            return;
        }

        try
        {
            var cfg = Plugin.Instance?.Configuration;
            var servedBefore = ServedChannels();
            var refs = _store.Lineup.Channels.Where(c => c.Enabled).SelectMany(c => c.Streams.Where(s => s.Enabled).Select(s => s.Ref)).Distinct(StringComparer.Ordinal).ToList();
            if (onlyUnknownOrDead)
            {
                refs = refs.Where(r => { var h = Get(r); return h.Unknown || h.IsDead; }).ToList();
            }

            if (onlySourceId is not null)
            {
                refs = refs.Where(r => r.StartsWith(onlySourceId + ":", StringComparison.Ordinal)).ToList();
            }

            var bySource = refs.GroupBy(r => r.Split(':', 2)[0], StringComparer.Ordinal).ToList();
            var total = refs.Count;
            var done = 0;
            _logger.LogInformation("Terebi: probing {Count} streams from {Sources} sources", total, bySource.Count);

            await Parallel.ForEachAsync(bySource, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (group, ct) =>
            {
                var source = _store.GetSource(group.Key);
                if (source is null || !source.Enabled)
                {
                    return;
                }

                IEnumerable<string> refsOfSource = group;
                if (source.MaxConnections == 1 && !(cfg?.ProbeSingleConnectionSources ?? false))
                {
                    // Probing everything would occupy the account's only slot for as long as the run takes and every
                    // viewer would be refused by the provider meanwhile. Playback attempts grade live streams instead;
                    // only streams already marked dead are re-checked, one at a time, so they can come back.
                    var deadOnly = group.Where(r => Get(r).IsDead).ToList();
                    _logger.LogInformation("Terebi: single-connection source '{Source}': re-checking {Dead} dead streams only ({Count} total); full probing can be enabled in settings", source.Name, deadOnly.Count, group.Count());
                    Interlocked.Add(ref done, group.Count() - deadOnly.Count);
                    refsOfSource = deadOnly;
                }

                var parallelism = source.MaxConnections == 1 ? 1 : Math.Max(1, cfg?.HealthCheckConcurrencyPerSource ?? 2);
                using var gate = new SemaphoreSlim(parallelism, parallelism);
                var tasks = refsOfSource.Select(async streamRef =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (source.MaxConnections == 1 && IsSourceBusy(source.Id))
                        {
                            return; // never steal the only slot from a viewer
                        }

                        var raw = _catalog.Find(streamRef);
                        if (raw is not null)
                        {
                            await ProbeAsync(raw, source, ct).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        gate.Release();
                        var n = Interlocked.Increment(ref done);
                        if (n % 25 == 0)
                        {
                            progress?.Report(100.0 * n / Math.Max(1, total));
                        }
                    }
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await SaveAsync(cancellationToken).ConfigureAwait(false);
            var dead = refs.Count(r => Get(r).IsDead);
            var failing = refs.Count(r => Get(r).ConsecutiveFailures > 0);
            _logger.LogInformation("Terebi: health check finished, {Failing} of {Total} streams failed this run, {Dead} marked dead", failing, total, dead);
            progress?.Report(100);

            // Channels that came back to life (or lost their last stream) must reach Jellyfin without waiting a day.
            var servedAfter = ServedChannels();
            if (!servedBefore.SetEquals(servedAfter) && (cfg?.AutoRefreshGuide ?? true))
            {
                _guide?.Request($"health check: {servedAfter.Except(servedBefore).Count()} channels back, {servedBefore.Except(servedAfter).Count()} gone");
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async Task<StreamHealth> ProbeAsync(RawStream raw, SourceDefinition source, CancellationToken cancellationToken)
    {
        var h = Get(raw.Ref);
        var started = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, Plugin.Instance?.Configuration.StreamStartTimeoutSeconds ?? 12)));
        if (!Uri.TryCreate(raw.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // Terebi relays over HTTP only; rtmp, rtsp, udp and friends can neither be probed nor served.
            Apply(h, false, 0, "unsupported scheme: " + (uri?.Scheme ?? "?"), "unsupported");
            return h;
        }

        try
        {
            var http = _httpClientFactory.CreateClient("Terebi");
            var ua = source.UserAgent ?? Plugin.Instance?.Configuration.UserAgent ?? "Mozilla/5.0";

            // Xtream panels answer for an offline channel with a looping placeholder clip ("black.ts", "offline.ts").
            // The HLS variant of the same stream names the clip, so ask it first: cheap and unambiguous.
            if (source.Type == SourceType.Xtream && PlaceholderProbe.TryHlsVariant(raw.Url, out var hlsUrl))
            {
                var placeholder = await PlaceholderProbe.CheckAsync(http, ua, hlsUrl, timeout.Token).ConfigureAwait(false);
                if (placeholder is not null)
                {
                    Apply(h, false, 0, placeholder, "placeholder");
                    return h;
                }
            }

            var (ok, kind, error) = await ProbeUrlAsync(http, ua, raw.Url, depth: 0, timeout.Token).ConfigureAwait(false);
            if (!ok && PlaceholderProbe.IsBusyStatus(kind))
            {
                // The provider refused because the slot is taken (458 / 429 / 503): says nothing about the stream.
                h.LastCheckUtc = DateTimeOffset.UtcNow;
                h.LastError = "busy: " + error;
                return h;
            }

            Apply(h, ok, (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds, error, kind);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Apply(h, false, 0, "timeout", "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UriFormatException or NotSupportedException)
        {
            Apply(h, false, 0, ex.Message, "error");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // One odd stream must never take the whole run (and its results) down with it.
            _logger.LogWarning(ex, "Terebi: probe of {Url} failed unexpectedly", Epg.EpgService.Redact(raw.Url));
            Apply(h, false, 0, ex.GetType().Name + ": " + ex.Message, "error");
        }

        return h;
    }

    /// <summary>Follows HLS master → media playlist → first segment; for anything else reads the first bytes.</summary>
    private static async Task<(bool Ok, string Kind, string? Error)> ProbeUrlAsync(HttpClient http, string userAgent, string url, int depth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (false, "http-" + (int)response.StatusCode, "HTTP " + (int)response.StatusCode);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await body.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            read += n;
            if (read >= 8192 && !LooksLikePlaylist(buffer, read))
            {
                break; // enough bytes to call it a live TS
            }
        }

        if (read == 0)
        {
            return (false, "empty", "empty response");
        }

        if (LooksLikePlaylist(buffer, read))
        {
            if (depth > 2)
            {
                return (false, "hls-loop", "playlist nesting too deep");
            }

            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            var baseUri = response.RequestMessage?.RequestUri ?? new Uri(url);
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var isMaster = lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal));
            var target = lines.FirstOrDefault(l => !l.StartsWith('#'));
            if (target is null)
            {
                return (false, "hls-empty", "playlist has no segments");
            }

            var next = new Uri(baseUri, target).ToString();
            var inner = await ProbeUrlAsync(http, userAgent, next, depth + 1, ct).ConfigureAwait(false);
            return (inner.Ok, isMaster ? "hls" : "hls", inner.Error);
        }

        // Transport stream sync byte, or any binary payload from a video content type.
        if (buffer[0] == 0x47 || contentType.Contains("video", StringComparison.OrdinalIgnoreCase) || contentType.Contains("octet", StringComparison.OrdinalIgnoreCase) || read >= 8192)
        {
            return (true, buffer[0] == 0x47 ? "ts" : "binary", null);
        }

        return (false, "unknown", "unrecognised payload (" + contentType + ")");
    }

    private static bool LooksLikePlaylist(byte[] buffer, int length) =>
        length >= 7 && buffer[0] == (byte)'#' && buffer[1] == (byte)'E' && buffer[2] == (byte)'X' && buffer[3] == (byte)'T' && buffer[4] == (byte)'M' && buffer[5] == (byte)'3' && buffer[6] == (byte)'U';

    private static void Apply(StreamHealth h, bool ok, int latencyMs, string? error, string? kind)
    {
        var now = DateTimeOffset.UtcNow;
        h.LastCheckUtc = now;
        h.Kind = kind ?? h.Kind;
        if (ok)
        {
            h.LastOkUtc = now;
            h.ConsecutiveFailures = 0;
            h.LatencyMs = latencyMs;
            h.LastError = null;
        }
        else
        {
            h.LastFailUtc = now;
            h.ConsecutiveFailures++;
            h.LastError = error;
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) => JsonFile.SaveAsync(_path, _health.Values.ToList(), cancellationToken);
}
