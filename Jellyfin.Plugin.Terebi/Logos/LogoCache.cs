using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Terebi.Storage;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Logos;

/// <summary>
/// Downloads channel logos once and keeps them under the data folder. Jellyfin then reads local files
/// (Live TV) or a fast local URL (channel folders) instead of hitting provider hosts, which are often slow
/// or dead and would otherwise stall the guide refresh and the web client.
/// </summary>
public sealed partial class LogoCache
{
    private const int MaxBytes = 3 * 1024 * 1024;
    private const int Concurrency = 8;
    private static readonly TimeSpan PerLogoTimeout = TimeSpan.FromSeconds(10);

    private readonly TerebiPaths _paths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<LogoCache> _logger;
    private readonly ConcurrentDictionary<string, Entry> _entries;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _dir;

    public LogoCache(TerebiPaths paths, IHttpClientFactory httpClientFactory, IServerApplicationHost appHost, ILogger<LogoCache> logger)
    {
        _paths = paths;
        _httpClientFactory = httpClientFactory;
        _appHost = appHost;
        _logger = logger;
        _dir = paths.Folder("logos");
        var stored = JsonFile.Load<Dictionary<string, Entry>>(IndexPath, () => new Dictionary<string, Entry>(StringComparer.Ordinal), logger);
        _entries = new ConcurrentDictionary<string, Entry>(stored, StringComparer.Ordinal);
        PlaceholderPath = ExtractPlaceholder();
    }

    /// <summary>Gets the neutral placeholder image used for channels without a usable logo (keeps Jellyfin away from stale remote image paths).</summary>
    public string PlaceholderPath { get; }

    public string PlaceholderUrl => $"{_appHost.GetApiUrlForLocalAccess()}/Terebi/Logo/{(File.Exists(Path.Combine(_dir, TileName(PlaceholderFileName))) ? TileName(PlaceholderFileName) : PlaceholderFileName)}";

    private const string PlaceholderFileName = "placeholder.png";

    private string IndexPath => Path.Combine(_dir, "cache.json");

    /// <summary>Local file for a remote logo, falling back to the placeholder.</summary>
    public string LocalPathOrPlaceholder(string? remoteUrl) => LocalPath(remoteUrl) ?? (File.Exists(Path.Combine(_dir, TileName(PlaceholderFileName))) ? Path.Combine(_dir, TileName(PlaceholderFileName)) : PlaceholderPath);

    /// <summary>Local URL for a remote logo, falling back to the placeholder.</summary>
    public string LocalUrlOrPlaceholder(string? remoteUrl) => LocalUrl(remoteUrl) ?? PlaceholderUrl;

    private string ExtractPlaceholder()
    {
        var path = Path.Combine(_dir, PlaceholderFileName);
        try
        {
            if (!File.Exists(path))
            {
                var name = typeof(LogoCache).Assembly.GetManifestResourceNames().First(n => n.EndsWith(".Logos.placeholder.png", StringComparison.Ordinal));
                using var res = typeof(LogoCache).Assembly.GetManifestResourceStream(name)!;
                using var file = File.Create(path);
                res.CopyTo(file);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Terebi: could not write the placeholder logo");
        }

        return path;
    }

    /// <summary>Local file for a remote logo (the card tile when rendered, else the raw download), or null when not cached.</summary>
    public string? LocalPath(string? remoteUrl)
    {
        var name = LocalFileName(remoteUrl);
        return name is null ? null : Path.Combine(_dir, name);
    }

    /// <summary>Local URL (server-local address) for a remote logo, or null when it is not cached.</summary>
    public string? LocalUrl(string? remoteUrl)
    {
        var name = LocalFileName(remoteUrl);
        return name is null ? null : $"{_appHost.GetApiUrlForLocalAccess()}/Terebi/Logo/{name}";
    }

    private string? LocalFileName(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || !_entries.TryGetValue(remoteUrl, out var e) || e.File is null)
        {
            return null;
        }

        var tile = TileName(e.File);
        if (File.Exists(Path.Combine(_dir, tile)))
        {
            return tile;
        }

        return File.Exists(Path.Combine(_dir, e.File)) ? e.File : null;
    }

    private static string TileName(string file) => "t-" + Path.GetFileNameWithoutExtension(file) + ".png";

    private bool _tilesUnavailable;

    /// <summary>Renders the card tile for a cached logo; silently keeps the raw file when Skia is not usable.</summary>
    private void RenderTile(string file)
    {
        if (_tilesUnavailable)
        {
            return;
        }

        var input = Path.Combine(_dir, file);
        var output = Path.Combine(_dir, TileName(file));
        try
        {
            if (!LogoTile.TryRender(input, output))
            {
                _logger.LogDebug("Terebi: could not decode logo {File} for a tile", file);
            }
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            _tilesUnavailable = true;
            _logger.LogWarning(ex, "Terebi: SkiaSharp is not available to this plugin, logo tiles are disabled");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Terebi: tile rendering failed for {File}", file);
        }
    }

    /// <summary>Renders tiles for cached logos that have none yet (one-off after an upgrade, cheap afterwards).</summary>
    public int EnsureTiles()
    {
        var made = 0;
        foreach (var e in _entries.Values.ToArray())
        {
            if (e.File is null || _tilesUnavailable)
            {
                continue;
            }

            if (!File.Exists(Path.Combine(_dir, TileName(e.File))) && File.Exists(Path.Combine(_dir, e.File)))
            {
                RenderTile(e.File);
                made++;
            }
        }

        if (!File.Exists(Path.Combine(_dir, TileName(PlaceholderFileName))) && File.Exists(PlaceholderPath))
        {
            RenderTile(PlaceholderFileName);
        }

        return made;
    }

    /// <summary>Resolves a cache file name (as used in local URLs) to its path and content type.</summary>
    public bool TryResolve(string fileName, out string fullPath, out string contentType)
    {
        fullPath = string.Empty;
        contentType = string.Empty;
        if (!FileNameRegex().IsMatch(fileName) && !string.Equals(fileName, PlaceholderFileName, StringComparison.Ordinal) && !string.Equals(fileName, TileName(PlaceholderFileName), StringComparison.Ordinal))
        {
            return false;
        }

        var path = Path.Combine(_dir, fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        fullPath = path;
        contentType = Path.GetExtension(fileName) switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        return true;
    }

    public (int Cached, int Failed, int Pending) Stats(IEnumerable<string?> remoteUrls)
    {
        int cached = 0, failed = 0, pending = 0;
        foreach (var url in remoteUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal))
        {
            if (_entries.TryGetValue(url!, out var e) && e.File is not null)
            {
                cached++;
            }
            else if (_entries.TryGetValue(url!, out e) && e.FailedUtc is not null)
            {
                failed++;
            }
            else
            {
                pending++;
            }
        }

        return (cached, failed, pending);
    }

    /// <summary>
    /// Downloads every logo that is not cached yet (failed ones again after their back-off, or right away
    /// with <paramref name="retryFailed"/>). Stops after <paramref name="budget"/> and returns how many
    /// logos were newly cached; the rest can be picked up by a later call.
    /// </summary>
    public async Task<int> WarmAsync(IReadOnlyCollection<string?> remoteUrls, TimeSpan budget, bool retryFailed, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var todo = remoteUrls
            .Where(u => !string.IsNullOrWhiteSpace(u) && Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Select(u => u!)
            .Distinct(StringComparer.Ordinal)
            .Where(u => NeedsDownload(u, now, retryFailed))
            .ToList();
        if (todo.Count == 0)
        {
            var made = EnsureTiles();
            if (made > 0)
            {
                _logger.LogInformation("Terebi: rendered {Tiles} logo tiles", made);
            }

            return 0;
        }

        using var budgetCts = budget == Timeout.InfiniteTimeSpan ? new CancellationTokenSource() : new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetCts.Token);
        var added = 0;
        var attempted = 0;
        _logger.LogInformation("Terebi: caching {Count} channel logos", todo.Count);
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = linked.Token }, async (url, ct) =>
        {
            Interlocked.Increment(ref attempted);
            if (await DownloadOneAsync(url, ct).ConfigureAwait(false))
            {
                Interlocked.Increment(ref added);
            }
        }).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await SaveAsync(CancellationToken.None).ConfigureAwait(false);
        var tiles = EnsureTiles();
        _logger.LogInformation("Terebi: logo cache: {Added} new, {Attempted} attempted, {Left} left for later, {Tiles} tiles rendered", added, attempted, todo.Count - attempted, tiles);
        return added;
    }

    /// <summary>Forgets cached logos no channel references any more (files included).</summary>
    public async Task PruneAsync(IEnumerable<string?> keep, CancellationToken cancellationToken)
    {
        var keepSet = new HashSet<string>(keep.Where(k => !string.IsNullOrWhiteSpace(k))!, StringComparer.Ordinal);
        var maxAge = TimeSpan.FromDays(Math.Max(1, Plugin.Instance?.Configuration.LogoCacheDays ?? 30));
        var removed = 0;
        foreach (var kv in _entries.ToArray())
        {
            var last = kv.Value.FetchedUtc ?? kv.Value.FailedUtc ?? DateTimeOffset.MinValue;
            if (keepSet.Contains(kv.Key) || last.Add(maxAge) > DateTimeOffset.UtcNow)
            {
                continue;
            }

            if (_entries.TryRemove(kv.Key, out var e) && e.File is not null)
            {
                try
                {
                    File.Delete(Path.Combine(_dir, e.File));
                }
                catch (IOException)
                {
                    // best effort
                }
            }

            removed++;
        }

        if (removed > 0)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool NeedsDownload(string url, DateTimeOffset now, bool retryFailed)
    {
        if (!_entries.TryGetValue(url, out var e))
        {
            return true;
        }

        if (e.File is not null)
        {
            return !File.Exists(Path.Combine(_dir, e.File));
        }

        if (retryFailed || e.FailedUtc is null)
        {
            return true;
        }

        // back off: 1 day after the first failure, up to a week
        var wait = TimeSpan.FromDays(Math.Min(7, Math.Max(1, e.Failures)));
        return e.FailedUtc.Value.Add(wait) < now;
    }

    private async Task<bool> DownloadOneAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(PerLogoTimeout);
            var http = _httpClientFactory.CreateClient("Terebi");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Hosts such as Wikimedia refuse anonymous or over-parallel clients: browser UA and two at a time per host.
            request.Headers.UserAgent.ParseAdd(Plugin.Instance?.Configuration.UserAgent ?? "Mozilla/5.0");
            request.Headers.Accept.ParseAdd("image/*");
            var host = new Uri(url).Host;
            var gate = _hostGates.GetOrAdd(host, _ => new SemaphoreSlim(2, 2));
            await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            using var release = new GateRelease(gate);
            HttpResponseMessage? response = null;
            try
            {
                // Rate-limited hosts (Wikimedia answers 429 quickly): pace requests and honour Retry-After a few times.
                for (var attempt = 1; ; attempt++)
                {
                    using var attemptRequest = new HttpRequestMessage(HttpMethod.Get, url);
                    attemptRequest.Headers.UserAgent.ParseAdd(request.Headers.UserAgent.ToString());
                    attemptRequest.Headers.Accept.ParseAdd("image/*");
                    response = await http.SendAsync(attemptRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    if ((int)response.StatusCode is 429 or 503 && attempt < 4)
                    {
                        var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1.5 * attempt);
                        response.Dispose();
                        response = null;
                        await Task.Delay(wait > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : wait, cts.Token).ConfigureAwait(false);
                        continue;
                    }

                    break;
                }

                if (!response!.IsSuccessStatusCode || (response.Content.Headers.ContentLength ?? 0) > MaxBytes)
                {
                    return Fail(url, "HTTP " + (int)response.StatusCode + (response.Content.Headers.ContentLength > MaxBytes ? " too large" : string.Empty));
                }


            await using var body = await response!.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await body.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxBytes)
                {
                    return Fail(url, "too large");
                }
            }

            var bytes = buffer.ToArray();
            var ext = SniffExtension(bytes, response.Content.Headers.ContentType?.MediaType);
            if (ext is null || bytes.Length < 32)
            {
                return Fail(url, "not an image (" + (response.Content.Headers.ContentType?.MediaType ?? "no content type") + ")");
            }

            var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ext;
            var final = Path.Combine(_dir, name);
            var tmp = final + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, cts.Token).ConfigureAwait(false);
            File.Move(tmp, final, overwrite: true);
            _entries[url] = new Entry { File = name, FetchedUtc = DateTimeOffset.UtcNow };
            RenderTile(name);
            return true;
            }
            finally
            {
                response?.Dispose();
                // keep a little distance between requests to the same host while still holding its slot
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400), CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // never happens with CancellationToken.None
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(url, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UriFormatException)
        {
            _logger.LogDebug(ex, "Terebi: logo download failed: {Url}", url);
            return Fail(url, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private bool Fail(string url, string reason)
    {
        _entries.AddOrUpdate(
            url,
            _ => new Entry { FailedUtc = DateTimeOffset.UtcNow, Failures = 1, LastError = reason },
            (_, old) => new Entry { FailedUtc = DateTimeOffset.UtcNow, Failures = old.Failures + 1, LastError = reason });
        return false;
    }

    private static string? SniffExtension(byte[] bytes, string? mediaType)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return ".png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'8')
        {
            return ".gif";
        }

        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return ".webp";
        }

        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
        if (head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return ".svg";
        }

        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            _ => null,
        };
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonFile.SaveAsync(IndexPath, new Dictionary<string, Entry>(_entries, StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    [GeneratedRegex(@"^(cat-|t-)?[0-9a-f]{40}\.(png|jpg|gif|webp|svg)$")]
    private static partial Regex FileNameRegex();

    private sealed class GateRelease : IDisposable
    {
        private readonly SemaphoreSlim _gate;

        public GateRelease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose() => _gate.Release();
    }

    private sealed class Entry
    {
        [JsonPropertyName("file")]
        public string? File { get; set; }

        [JsonPropertyName("fetchedUtc")]
        public DateTimeOffset? FetchedUtc { get; set; }

        [JsonPropertyName("failedUtc")]
        public DateTimeOffset? FailedUtc { get; set; }

        [JsonPropertyName("failures")]
        public int Failures { get; set; }

        [JsonPropertyName("lastError")]
        public string? LastError { get; set; }
    }
}
