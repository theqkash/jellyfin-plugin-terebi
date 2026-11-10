using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Terebi.Catalog;

namespace Jellyfin.Plugin.Terebi.Sources;

/// <summary>
/// Parses extended M3U playlists (#EXTINF with tvg-* attributes) into raw catalog entries.
/// </summary>
public static partial class M3uReader
{
    public static List<RawStream> Parse(string content, string sourceId)
    {
        var list = new List<RawStream>();
        string? extinf = null;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                extinf = line;
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            if (extinf is null)
            {
                continue;
            }

            var attrs = ParseAttributes(extinf);
            var comma = extinf.LastIndexOf(',');
            var title = comma >= 0 ? extinf[(comma + 1)..].Trim() : string.Empty;
            attrs.TryGetValue("tvg-name", out var tvgName);
            attrs.TryGetValue("tvg-id", out var tvgId);
            attrs.TryGetValue("tvg-logo", out var logo);
            attrs.TryGetValue("group-title", out var group);
            attrs.TryGetValue("tvg-chno", out var chno);
            attrs.TryGetValue("catchup-days", out var catchupDays);

            list.Add(new RawStream
            {
                SourceId = sourceId,
                Key = Hash(line),
                Name = string.IsNullOrWhiteSpace(title) ? tvgName ?? line : title,
                Category = group ?? string.Empty,
                Logo = string.IsNullOrWhiteSpace(logo) ? null : logo,
                EpgId = string.IsNullOrWhiteSpace(tvgId) ? null : tvgId,
                Url = line,
                Number = int.TryParse(chno, out var n) ? n : null,
                CatchupDays = int.TryParse(catchupDays, out var cd) ? cd : 0,
            });
            extinf = null;
        }

        // The same URL listed twice is one stream: keep the first entry so keys stay unique per source.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        list.RemoveAll(s => !seen.Add(s.Key));
        return list;
    }

    /// <summary>XMLTV addresses announced in the playlist header (url-tvg / x-tvg-url), comma separated.</summary>
    public static List<string> TvgUrls(string content)
    {
        var head = content.Length > 4000 ? content[..4000] : content;
        var line = head.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var m = System.Text.RegularExpressions.Regex.Match(line, "(?:url-tvg|x-tvg-url)=\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return [];
        }

        return m.Groups[1].Value.Split(',').Select(u => u.Trim()).Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    internal static Dictionary<string, string> ParseAttributes(string extinf)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttributeRegex().Matches(extinf))
        {
            d[m.Groups["k"].Value] = m.Groups["v"].Value;
        }

        return d;
    }

    private static string Hash(string url)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(url.Trim()));
        return Convert.ToHexString(bytes, 0, 10).ToLowerInvariant();
    }

    [GeneratedRegex(@"(?<k>[A-Za-z0-9\-_]+)=""(?<v>[^""]*)""")]
    private static partial Regex AttributeRegex();
}
