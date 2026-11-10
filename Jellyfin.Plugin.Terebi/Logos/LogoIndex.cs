using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Logos;

/// <summary>
/// Curated channel logos from the iptv-org database (channels.json + logos.json), reduced to a small
/// name-key to logo map per country. IPTV providers often ship wrong or dead logos; this index gives
/// every channel a correct one when the name and country can be matched.
/// </summary>
public sealed partial class LogoIndex
{
    private const string ChannelsUrl = "https://iptv-org.github.io/api/channels.json";
    private const string LogosUrl = "https://iptv-org.github.io/api/logos.json";
    private const int RefreshDays = 7;

    private readonly TerebiPaths _paths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LogoIndex> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IndexFile? _index;
    private bool _loadedFromDisk;

    public LogoIndex(TerebiPaths paths, IHttpClientFactory httpClientFactory, ILogger<LogoIndex> logger)
    {
        _paths = paths;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public DateTimeOffset? FetchedUtc => _index?.FetchedUtc;

    public int Count => _index?.Entries.Count ?? 0;

    private string FilePath => Path.Combine(_paths.Folder("logos"), "iptv-org-index.json");

    /// <summary>Loads the index from disk and downloads a fresh copy when it is missing or older than a week. Best effort.</summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (!(Plugin.Instance?.Configuration.EnableLogoIndex ?? true))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loadedFromDisk)
            {
                _index = JsonFile.Load<IndexFile?>(FilePath, () => null, _logger);
                _loadedFromDisk = true;
            }

            if (_index is not null && _index.ById.Count > 0 && _index.UrlToId.Count > 0 && _index.FetchedUtc.AddDays(RefreshDays) > DateTimeOffset.UtcNow)
            {
                return;
            }

            try
            {
                var fresh = await DownloadAsync(cancellationToken).ConfigureAwait(false);
                await JsonFile.SaveAsync(FilePath, fresh, cancellationToken).ConfigureAwait(false);
                _index = fresh;
                _logger.LogInformation("Terebi: logo index refreshed, {Keys} name keys", fresh.Entries.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or IOException)
            {
                _logger.LogWarning(ex, "Terebi: logo index download failed, keeping the previous copy ({Count} keys)", _index?.Entries.Count ?? 0);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Logo for a channel name. With a country code only that country's channels count; without one the
    /// name has to be unambiguous across the whole database.
    /// </summary>
    public string? Find(string name, string? country, IEnumerable<string>? epgIds = null)
    {
        var index = _index;
        if (index is null)
        {
            return null;
        }

        // 1. iptv-org channel id carried by the XMLTV id ("TVP1.pl@SD" -> "TVP1.pl"): exact.
        if (epgIds is not null)
        {
            foreach (var raw in epgIds)
            {
                var id = raw;
                var at = id.IndexOf('@', StringComparison.Ordinal);
                if (at > 0)
                {
                    id = id[..at];
                }

                if (index.ById.TryGetValue(id, out var byId))
                {
                    return byId;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // 2. normalised name, then the same without spaces ("TVN 24" vs "TVN24"), then without a trailing
        //    country word in either language ("AMC POLSKA" / "AMC Poland" / "AMC").
        var key = NameNormalizer.Parse(name).Key;
        foreach (var k in KeyVariants(key, country))
        {
            var hit = Lookup(index.Entries, k, country) ?? Lookup(index.Compact, k.Replace(" ", string.Empty, StringComparison.Ordinal), country);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static readonly Dictionary<string, string[]> CountryWords = new(StringComparer.Ordinal)
    {
        ["PL"] = ["POLAND", "POLSKA", "PL"],
        ["JP"] = ["JAPAN", "JP"],
        ["KR"] = ["KOREA", "KR"],
        ["CN"] = ["CHINA", "CN"],
        ["DE"] = ["GERMANY", "DEUTSCHLAND", "DE"],
        ["FR"] = ["FRANCE", "FR"],
        ["ES"] = ["SPAIN", "ESPANA", "ES"],
        ["IT"] = ["ITALY", "ITALIA", "IT"],
        ["GB"] = ["UK", "GB"],
        ["US"] = ["USA", "US"],
        ["CZ"] = ["CZECH", "CZ"],
        ["HU"] = ["HUNGARY", "HU"],
    };

    /// <summary>The key itself, then with a trailing country word swapped for each synonym, then without it.</summary>
    public static IEnumerable<string> KeyVariants(string key, string? country)
    {
        yield return key;
        var words = country is not null && CountryWords.TryGetValue(country.ToUpperInvariant(), out var w) ? w : CountryWords.Values.SelectMany(x => x).ToArray();
        foreach (var word in words)
        {
            if (key.EndsWith(" " + word, StringComparison.Ordinal))
            {
                var stem = key[..^(word.Length + 1)];
                foreach (var other in words.Where(o => !string.Equals(o, word, StringComparison.Ordinal)))
                {
                    yield return stem + " " + other;
                }

                yield return stem;
                yield break;
            }
        }

        if (country is not null && CountryWords.TryGetValue(country.ToUpperInvariant(), out var mine))
        {
            foreach (var word in mine)
            {
                yield return key + " " + word;
            }
        }
    }

    private static string? Lookup(Dictionary<string, List<IndexEntry>> map, string key, string? country)
    {
        if (key.Length < 2 || !map.TryGetValue(key, out var candidates) || candidates.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(country))
        {
            var c = country.ToUpperInvariant();
            return candidates.FirstOrDefault(e => string.Equals(e.C, c, StringComparison.Ordinal))?.U;
        }

        var urls = candidates.Select(e => e.U).Distinct(StringComparer.Ordinal).ToList();
        return urls.Count == 1 ? urls[0] : null;
    }

    /// <summary>The iptv-org channel id a name (or XMLTV id) resolves to, or null. Two streams with the same identity are the same channel.</summary>
    public string? IdentityOf(string name, string? country, IEnumerable<string>? epgIds = null)
    {
        var index = _index;
        if (index is null)
        {
            return null;
        }

        var url = Find(name, country, epgIds);
        return url is not null && index.UrlToId.TryGetValue(url, out var id) ? id : null;
    }

    /// <summary>Alternative names (native script included) of the iptv-org channel the given name and country resolve to; empty when unknown.</summary>
    public IReadOnlyList<string> NamesOf(string name, string? country, IEnumerable<string>? epgIds = null)
    {
        var index = _index;
        if (index is null)
        {
            return [];
        }

        var url = Find(name, country, epgIds);
        if (url is null || !index.UrlToId.TryGetValue(url, out var id) || !index.NamesById.TryGetValue(id, out var names))
        {
            return [];
        }

        return names;
    }

    /// <summary>Two-letter country code for a channel: region prefix, XMLTV id suffix, category or source label.</summary>
    public static string? CountryOf(LogicalChannel channel, string? sourceLabel = null)
    {
        foreach (var candidate in new[] { channel.Region, channel.Category, sourceLabel })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var m = LeadingCountryRegex().Match(candidate);
                if (m.Success)
                {
                    return m.Groups["c"].Value.ToUpperInvariant();
                }
            }
        }

        foreach (var id in channel.EpgIds)
        {
            var m = EpgCountryRegex().Match(id);
            if (m.Success)
            {
                return m.Groups["c"].Value.ToUpperInvariant();
            }
        }

        return null;
    }

    private async Task<IndexFile> DownloadAsync(CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient("Terebi");
        http.Timeout = TimeSpan.FromSeconds(120);

        using var channelsDoc = JsonDocument.Parse(await http.GetByteArrayAsync(ChannelsUrl, cancellationToken).ConfigureAwait(false));
        using var logosDoc = JsonDocument.Parse(await http.GetByteArrayAsync(LogosUrl, cancellationToken).ConfigureAwait(false));

        // Best logo per channel id: raster before vector, sensible size, then the largest.
        var bestLogo = new Dictionary<string, (int Score, string Url)>(StringComparer.Ordinal);
        foreach (var l in logosDoc.RootElement.EnumerateArray())
        {
            var channel = l.TryGetProperty("channel", out var ch) ? ch.GetString() : null;
            var url = l.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (l.TryGetProperty("feed", out var feed) && feed.ValueKind == JsonValueKind.String)
            {
                continue; // feed-specific variants (4K, regional) are noise here
            }

            var format = l.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()!.ToUpperInvariant() : string.Empty;
            var width = l.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
            var formatRank = format switch { "PNG" => 0, "WEBP" => 1, "JPEG" => 2, "JPG" => 2, "SVG" => 3, "GIF" => 4, _ => 5 };
            var sizeRank = width is >= 150 and <= 1600 ? 0 : 1;
            var score = (formatRank * 100000) + (sizeRank * 10000) + Math.Max(0, 5000 - Math.Min(width, 5000));
            if (!bestLogo.TryGetValue(channel, out var cur) || score < cur.Score)
            {
                bestLogo[channel] = (score, url);
            }
        }

        var entries = new ConcurrentDictionary<string, List<IndexEntry>>(StringComparer.Ordinal);
        var compact = new ConcurrentDictionary<string, List<IndexEntry>>(StringComparer.Ordinal);
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var namesById = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var urlToId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in channelsDoc.RootElement.EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            if (id is null || !bestLogo.TryGetValue(id, out var logo))
            {
                continue;
            }

            byId[id] = logo.Url;
            urlToId.TryAdd(logo.Url, id);

            var country = c.TryGetProperty("country", out var co) && co.ValueKind == JsonValueKind.String ? co.GetString()!.ToUpperInvariant() : string.Empty;
            var closed = c.TryGetProperty("closed", out var cl) && cl.ValueKind == JsonValueKind.String;
            var names = new List<string>();
            if (c.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            {
                names.Add(n.GetString()!);
            }

            if (c.TryGetProperty("alt_names", out var alt) && alt.ValueKind == JsonValueKind.Array)
            {
                names.AddRange(alt.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!));
            }

            foreach (var key in names.Select(x => NameNormalizer.Parse(x).Key).Where(k => k.Length >= 2).Distinct(StringComparer.Ordinal))
            {
                Add(entries, key, country, logo.Url, closed);
                Add(compact, key.Replace(" ", string.Empty, StringComparison.Ordinal), country, logo.Url, closed);
            }

            if (names.Count > 1)
            {
                namesById[id] = names.Distinct(StringComparer.Ordinal).ToList();
            }
        }

        return new IndexFile
        {
            FetchedUtc = DateTimeOffset.UtcNow,
            Entries = new Dictionary<string, List<IndexEntry>>(entries, StringComparer.Ordinal),
            Compact = new Dictionary<string, List<IndexEntry>>(compact, StringComparer.Ordinal),
            ById = byId,
            NamesById = namesById,
            UrlToId = urlToId,
        };
    }

    private static void Add(ConcurrentDictionary<string, List<IndexEntry>> map, string key, string country, string url, bool closed)
    {
        var list = map.GetOrAdd(key, _ => []);
        lock (list)
        {
            // open channels first, so a closed namesake never shadows the live one
            if (closed)
            {
                list.Add(new IndexEntry { C = country, U = url });
            }
            else
            {
                list.Insert(0, new IndexEntry { C = country, U = url });
            }
        }
    }

    [GeneratedRegex(@"^\s*(?<c>[A-Za-z]{2})(?![A-Za-z])")]
    private static partial Regex LeadingCountryRegex();

    [GeneratedRegex(@"\.(?<c>[A-Za-z]{2})(?:@|$)")]
    private static partial Regex EpgCountryRegex();

    private sealed class IndexFile
    {
        [JsonPropertyName("fetchedUtc")]
        public DateTimeOffset FetchedUtc { get; set; }

        [JsonPropertyName("entries")]
        public Dictionary<string, List<IndexEntry>> Entries { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("compact")]
        public Dictionary<string, List<IndexEntry>> Compact { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("byId")]
        public Dictionary<string, string> ById { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets or sets all names (name + alt_names) per iptv-org channel id, for channels that have more than one.</summary>
        [JsonPropertyName("namesById")]
        public Dictionary<string, List<string>> NamesById { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("urlToId")]
        public Dictionary<string, string> UrlToId { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class IndexEntry
    {
        /// <summary>Gets or sets the country code.</summary>
        [JsonPropertyName("c")]
        public string C { get; set; } = string.Empty;

        /// <summary>Gets or sets the logo url.</summary>
        [JsonPropertyName("u")]
        public string U { get; set; } = string.Empty;
    }
}
