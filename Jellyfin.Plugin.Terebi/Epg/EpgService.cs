using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Xml;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Sources;
using Jellyfin.Plugin.Terebi.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Epg;

public sealed class EpgProgram
{
    [JsonPropertyName("s")]
    public DateTimeOffset Start { get; set; }

    [JsonPropertyName("e")]
    public DateTimeOffset End { get; set; }

    [JsonPropertyName("t")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("st")]
    public string? SubTitle { get; set; }

    [JsonPropertyName("d")]
    public string? Description { get; set; }

    [JsonPropertyName("c")]
    public string? Category { get; set; }

    [JsonPropertyName("i")]
    public string? Icon { get; set; }

    [JsonPropertyName("sn")]
    public int? Season { get; set; }

    [JsonPropertyName("en")]
    public int? Episode { get; set; }
}

/// <summary>A downloaded XMLTV file: which channel ids it has and their display names (for the mapping UI).</summary>
public sealed class EpgSourceInfo
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("fetchedUtc")]
    public DateTimeOffset? FetchedUtc { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("channels")]
    public Dictionary<string, string> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets channel icon urls declared by the feed, by channel id.</summary>
    [JsonPropertyName("icons")]
    public Dictionary<string, string> Icons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("programmes")]
    public int Programmes { get; set; }
}

/// <summary>
/// EPG from two kinds of places: XMLTV files (global list + per source) parsed with a streaming reader so
/// 100 MB feeds do not blow up memory, and Xtream's per-stream short EPG for channels without XMLTV.
/// Programs are kept in memory per channel id; only ids referenced by the lineup are stored.
/// </summary>
public sealed class EpgService
{
    private readonly LineupStore _store;
    private readonly XtreamClient _xtream;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EpgService> _logger;
    private readonly string _folder;
    private readonly ConcurrentDictionary<string, List<EpgProgram>> _programs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedUtc, List<EpgProgram> Items)> _shortEpg = new(StringComparer.Ordinal);
    private List<EpgSourceInfo> _sources;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EpgService(TerebiPaths paths, LineupStore store, XtreamClient xtream, IHttpClientFactory httpClientFactory, ILogger<EpgService> logger)
    {
        _store = store;
        _xtream = xtream;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _folder = paths.Folder("epg");
        _sources = JsonFile.Load(Path.Combine(_folder, "sources.json"), () => new List<EpgSourceInfo>(), logger);
        var cached = JsonFile.Load(Path.Combine(_folder, "programs.json"), () => new Dictionary<string, List<EpgProgram>>(), logger);
        foreach (var (k, v) in cached)
        {
            _programs[k] = v;
        }
    }

    /// <summary>Gets or sets the global XMLTV URLs (not tied to one source), kept in data/terebi/epg/urls.json.</summary>
    public List<string> GlobalUrls
    {
        get => JsonFile.Load(Path.Combine(_folder, "urls.json"), () => new List<string>(), _logger);
        set => JsonFile.SaveAsync(Path.Combine(_folder, "urls.json"), value).GetAwaiter().GetResult();
    }

    public IReadOnlyList<EpgSourceInfo> Sources => _sources;

    /// <summary>All channel ids known from XMLTV files with display names, for the mapping editor.</summary>
    public IReadOnlyDictionary<string, string> KnownChannels()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _sources)
        {
            foreach (var (id, name) in s.Channels)
            {
                d.TryAdd(id, name);
            }
        }

        return d;
    }

    public bool HasPrograms(string epgId) => _programs.TryGetValue(epgId, out var l) && l.Count > 0;

    /// <summary>Country code carried by an XMLTV id ("ca.CJON", "TVP1.pl@SD"), or null.</summary>
    private static string? CountryOfId(string id)
    {
        var m = System.Text.RegularExpressions.Regex.Match(id, @"^(?<c>[A-Za-z]{2})\.[^.]|\.(?<c>[A-Za-z]{2})(?:@|$)");
        return m.Success ? m.Groups["c"].Value.ToUpperInvariant() : null;
    }

    /// <summary>Icon declared by an XMLTV feed for one of the channel's ids, else for a channel whose name has the same key.</summary>
    public string? FindIcon(IEnumerable<string> epgIds, string nameKey, string? country = null)
    {
        foreach (var id in epgIds)
        {
            foreach (var src in _sources)
            {
                if (src.Icons.TryGetValue(id, out var icon))
                {
                    return icon;
                }
            }
        }

        if (nameKey.Length < 2)
        {
            return null;
        }

        var byKey = ChannelsByKey();
        foreach (var variant in Logos.LogoIndex.KeyVariants(nameKey, country))
        {
            if (!byKey.TryGetValue(variant, out var ids))
            {
                continue;
            }

            foreach (var id in ids)
            {
                foreach (var src in _sources)
                {
                    if (src.Icons.TryGetValue(id, out var icon))
                    {
                        return icon;
                    }
                }
            }
        }

        return null;
    }

    private Dictionary<string, List<string>>? _byKeyCache;
    private List<EpgSourceInfo>? _byKeyCacheFor;

    /// <summary>Normalised name key (from display name and from the id) to XMLTV channel ids, rebuilt when feeds change.</summary>
    private Dictionary<string, List<string>> ChannelsByKey()
    {
        var sources = _sources;
        if (_byKeyCache is not null && ReferenceEquals(_byKeyCacheFor, sources))
        {
            return _byKeyCache;
        }

        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var src in sources)
        {
            foreach (var (id, name) in src.Channels)
            {
                foreach (var key in new[] { NameNormalizer.Parse(name).Key, NameNormalizer.Parse(id.Split('@')[0].Replace('.', ' ')).Key }.Distinct(StringComparer.Ordinal))
                {
                    if (key.Length < 2)
                    {
                        continue;
                    }

                    if (!map.TryGetValue(key, out var list))
                    {
                        map[key] = list = [];
                    }

                    if (!list.Contains(id, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(id);
                    }
                }
            }
        }

        _byKeyCache = map;
        _byKeyCacheFor = sources;
        return map;
    }

    /// <summary>
    /// XMLTV channel id for a channel name when exactly one feed channel has the same key (ids with programmes
    /// preferred). Used to map channels the providers left without an EPG id.
    /// </summary>
    public string? SuggestIdByName(string nameKey, string? country = null)
    {
        if (nameKey.Length < 2)
        {
            return null;
        }

        var byKey = ChannelsByKey();
        foreach (var variant in Logos.LogoIndex.KeyVariants(nameKey, country))
        {
            if (!byKey.TryGetValue(variant, out var all) || all.Count == 0)
            {
                continue;
            }

            // An XMLTV id that names another country ("ca.CJON", "NTV.ca") is not this channel.
            var hits = all.Where(id => CountryOfId(id) is not { } c || country is null || string.Equals(c, country, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
            {
                continue;
            }

            var withPrograms = hits.Where(HasPrograms).ToList();
            if (withPrograms.Count > 0)
            {
                return withPrograms[0];
            }

            if (hits.Count == 1)
            {
                return hits[0];
            }
        }

        return null;
    }

    /// <summary>The programme airing now from XMLTV data (no network call); null when unknown.</summary>
    /// <summary>Current and next programme of a channel from the best available source.</summary>
    public (EpgProgram? Now, EpgProgram? Next) GetNowNext(LogicalChannel channel, DateTimeOffset now)
    {
        IReadOnlyList<EpgProgram>? list = null;
        foreach (var id in channel.EpgIds)
        {
            if (_programs.TryGetValue(id, out var l) && l.Count > 0)
            {
                list = l;
                break;
            }
        }

        if (list is null)
        {
            foreach (var sr in channel.Streams)
            {
                if (_shortEpg.TryGetValue(sr.Ref, out var cached) && cached.Items.Count > 0)
                {
                    list = cached.Items;
                    break;
                }
            }
        }

        if (list is null)
        {
            return (null, null);
        }

        var current = list.FirstOrDefault(p => p.Start <= now && p.End > now);
        var next = list.Where(p => p.Start > now).OrderBy(p => p.Start).FirstOrDefault();
        return (current, next);
    }

    public EpgProgram? GetCurrentProgram(LogicalChannel channel, DateTimeOffset now)
    {
        foreach (var id in channel.EpgIds)
        {
            if (_programs.TryGetValue(id, out var list) && list.Count > 0)
            {
                return list.FirstOrDefault(p => p.Start <= now && p.End > now);
            }
        }

        foreach (var sr in channel.Streams)
        {
            if (_shortEpg.TryGetValue(sr.Ref, out var cached))
            {
                return cached.Items.FirstOrDefault(p => p.Start <= now && p.End > now);
            }
        }

        return null;
    }

    /// <summary>Programs for a logical channel: first XMLTV id with data wins, else Xtream short EPG of the primary stream.</summary>
    public async Task<IReadOnlyList<EpgProgram>> GetProgramsAsync(LogicalChannel channel, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        foreach (var id in channel.EpgIds)
        {
            if (_programs.TryGetValue(id, out var list) && list.Count > 0)
            {
                return list.Where(p => p.End > from && p.Start < to).ToList();
            }
        }

        // Fallback: Xtream short EPG from the first Xtream stream of the channel (cached 30 minutes).
        foreach (var sr in channel.Streams.Where(s => s.Enabled))
        {
            var source = _store.GetSource(sr.SourceId);
            if (source is null || source.Type != SourceType.Xtream)
            {
                continue;
            }

            var streamId = sr.Ref.Split(':', 2)[1];
            if (_shortEpg.TryGetValue(sr.Ref, out var cached) && cached.FetchedUtc.AddMinutes(30) > DateTimeOffset.UtcNow)
            {
                return cached.Items.Where(p => p.End > from && p.Start < to).ToList();
            }

            try
            {
                var entries = await _xtream.GetShortEpgAsync(source, streamId, 48, cancellationToken).ConfigureAwait(false);
                var items = entries.Select(e => new EpgProgram { Start = e.Start, End = e.End, Title = e.Title, Description = e.Description }).ToList();
                _shortEpg[sr.Ref] = (DateTimeOffset.UtcNow, items);
                if (items.Count > 0)
                {
                    return items.Where(p => p.End > from && p.Start < to).ToList();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Terebi: short EPG failed for {Ref}", sr.Ref);
            }
        }

        return [];
    }

    /// <summary>Hides account credentials in provider XMLTV urls before they reach logs or the UI.</summary>
    public static string Redact(string url) => System.Text.RegularExpressions.Regex.Replace(url, @"(?i)((?:username|password|user|pass|token)=)[^&]*", "$1***");

    /// <summary>Downloads every XMLTV url (global + per source) and rebuilds the in-memory program table.</summary>
    public async Task RefreshAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var enabledSources = _store.Sources.Where(s => s.Enabled).ToList();
            var urls = GlobalUrls
                .Concat(enabledSources.SelectMany(s => s.EpgUrls))
                .Concat(enabledSources.Where(s => s.Type == SourceType.M3u && s.UseProviderEpg && s.EpgUrls.Count == 0).SelectMany(s => s.DiscoveredEpgUrls))
                .Concat(enabledSources.Where(s => s.Type == SourceType.Xtream && s.UseProviderEpg && !string.IsNullOrWhiteSpace(s.Url)).Select(XtreamClient.XmltvUrl))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var wanted = new HashSet<string>(_store.Lineup.Channels.SelectMany(c => c.EpgIds), StringComparer.OrdinalIgnoreCase);
            var fresh = new ConcurrentDictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);
            var infos = new List<EpgSourceInfo>();
            for (var i = 0; i < urls.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new EpgSourceInfo { Url = Redact(urls[i]) };
                try
                {
                    await ParseAsync(urls[i], wanted, fresh, info, cancellationToken).ConfigureAwait(false);
                    info.FetchedUtc = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Terebi: EPG {Url}: {Channels} channels, {Programmes} programmes kept", info.Url, info.Channels.Count, info.Programmes);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or InvalidDataException or TaskCanceledException)
                {
                    info.Error = ex.Message;
                    _logger.LogWarning(ex, "Terebi: EPG {Url} failed", urls[i]);
                }

                infos.Add(info);
                progress?.Report(90.0 * (i + 1) / Math.Max(1, urls.Count));
            }

            // Keep old data for ids that failed this time so a flaky feed does not empty the guide.
            foreach (var (id, list) in fresh)
            {
                _programs[id] = list.OrderBy(p => p.Start).ToList();
            }

            _sources = infos;
            await JsonFile.SaveAsync(Path.Combine(_folder, "sources.json"), _sources, cancellationToken).ConfigureAwait(false);
            await JsonFile.SaveAsync(Path.Combine(_folder, "programs.json"), _programs.ToDictionary(k => k.Key, v => v.Value), cancellationToken).ConfigureAwait(false);
            progress?.Report(100);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ParseAsync(string url, HashSet<string> wanted, ConcurrentDictionary<string, List<EpgProgram>> into, EpgSourceInfo info, CancellationToken cancellationToken)
    {
        Stream raw;
        if (url.Contains("://", StringComparison.Ordinal))
        {
            var http = _httpClientFactory.CreateClient("Terebi");
            http.Timeout = TimeSpan.FromMinutes(5);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(Plugin.Instance?.Configuration.UserAgent ?? "Mozilla/5.0");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            raw = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            raw = File.OpenRead(url);
        }

        await using (raw)
        {
            // Peek for gzip magic regardless of extension.
            var buffered = new BufferedStream(raw, 1 << 16);
            var head = new byte[2];
            var n = await buffered.ReadAsync(head, cancellationToken).ConfigureAwait(false);
            Stream xmlStream = new ConcatStream(head.AsMemory(0, n), buffered);
            if (n == 2 && head[0] == 0x1f && head[1] == 0x8b)
            {
                xmlStream = new GZipStream(xmlStream, CompressionMode.Decompress);
            }

            var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true };
            using var reader = XmlReader.Create(xmlStream, settings);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Name == "channel")
                {
                    var id = reader.GetAttribute("id") ?? string.Empty;
                    string? name = null;
                    string? icon = null;
                    using var sub = reader.ReadSubtree();
                    while (sub.Read())
                    {
                        if (sub.NodeType != XmlNodeType.Element)
                        {
                            continue;
                        }

                        if (sub.Name == "display-name" && name is null)
                        {
                            name = sub.ReadElementContentAsString();
                        }
                        else if (sub.Name == "icon" && icon is null)
                        {
                            icon = sub.GetAttribute("src");
                        }
                    }

                    if (id.Length > 0)
                    {
                        info.Channels.TryAdd(id, name ?? id);
                        if (!string.IsNullOrWhiteSpace(icon) && icon.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            info.Icons.TryAdd(id, icon);
                        }
                    }
                }
                else if (reader.Name == "programme")
                {
                    var channel = reader.GetAttribute("channel") ?? string.Empty;
                    var start = ParseXmltvTime(reader.GetAttribute("start"));
                    var stop = ParseXmltvTime(reader.GetAttribute("stop"));
                    using var sub = reader.ReadSubtree();
                    if (!wanted.Contains(channel) || start is null || stop is null)
                    {
                        sub.Read();
                        continue;
                    }

                    var p = new EpgProgram { Start = start.Value, End = stop.Value };
                    while (sub.Read())
                    {
                        if (sub.NodeType != XmlNodeType.Element)
                        {
                            continue;
                        }

                        switch (sub.Name)
                        {
                            case "title": p.Title = sub.ReadElementContentAsString(); break;
                            case "sub-title": p.SubTitle = sub.ReadElementContentAsString(); break;
                            case "desc": p.Description ??= sub.ReadElementContentAsString(); break;
                            case "category": p.Category ??= sub.ReadElementContentAsString(); break;
                            case "icon": p.Icon ??= sub.GetAttribute("src"); break;
                            case "episode-num":
                                var system = sub.GetAttribute("system");
                                var value = sub.ReadElementContentAsString();
                                if (system == "xmltv_ns")
                                {
                                    var parts = value.Split('.');
                                    if (parts.Length >= 2)
                                    {
                                        if (int.TryParse(parts[0].Split('/')[0], out var s)) { p.Season = s + 1; }
                                        if (int.TryParse(parts[1].Split('/')[0], out var e)) { p.Episode = e + 1; }
                                    }
                                }

                                break;
                        }
                    }

                    if (p.Title.Length > 0)
                    {
                        into.GetOrAdd(channel, _ => []).Add(p);
                        info.Programmes++;
                    }
                }
            }
        }
    }

    internal static DateTimeOffset? ParseXmltvTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        var formats = new[] { "yyyyMMddHHmmss zzz", "yyyyMMddHHmmss zz", "yyyyMMddHHmmss", "yyyyMMddHHmm zzz", "yyyyMMddHHmm", "yyyyMMddHH", "yyyyMMdd" };
        // .NET wants "+0200" as "zzz" only with a colon; normalise "+0200" → "+02:00".
        var space = v.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0 && v.Length - space == 6)
        {
            v = v[..(space + 4)] + ":" + v[(space + 4)..];
        }

        return DateTimeOffset.TryParseExact(v, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto) ? dto : null;
    }

    /// <summary>Re-attaches bytes that were peeked from a stream.</summary>
    private sealed class ConcatStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _head;
        private readonly Stream _rest;
        private int _headPos;

        public ConcatStream(ReadOnlyMemory<byte> head, Stream rest)
        {
            _head = head;
            _rest = rest;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_headPos < _head.Length)
            {
                var n = Math.Min(count, _head.Length - _headPos);
                _head.Span.Slice(_headPos, n).CopyTo(buffer.AsSpan(offset, n));
                _headPos += n;
                return n;
            }

            return _rest.Read(buffer, offset, count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rest.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
