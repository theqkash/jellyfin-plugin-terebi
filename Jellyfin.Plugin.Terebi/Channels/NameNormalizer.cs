using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Terebi.Channels;

/// <summary>
/// Turns provider names such as "XX-TAG| NEWS 7 RAW", "XX| NEWS 7 FHD", "NEWS 7 HD [Not 24/7]" into one
/// matching key ("NEWS 7") and a readable display name, so the variants of one station join one channel.
/// </summary>
public static partial class NameNormalizer
{
    /// <param name="Key">Matching key after alias resolution.</param>
    /// <param name="DisplayName">Readable name.</param>
    /// <param name="Variant">Quality variant tokens or "default".</param>
    /// <param name="RegionPrefix">Region prefix such as PL or JP, if any.</param>
    /// <param name="Flags">Bracketed tags found in the name.</param>
    /// <param name="RawKey">Matching key before alias resolution (equals Key unless an alias applied).</param>
    public sealed record Parsed(string Key, string DisplayName, string Variant, string? RegionPrefix, IReadOnlyList<string> Flags, string RawKey);

    public static Parsed Parse(string rawName)
    {
        var name = rawName.Normalize(NormalizationForm.FormKC).Trim();
        string? region = null;
        var m = PrefixRegex().Match(name);
        if (m.Success)
        {
            region = m.Groups["p"].Success && m.Groups["p"].Length > 0 ? m.Groups["p"].Value.ToUpperInvariant() : null;
            if (region is not null && RegionAliases.TryGetValue(region, out var alias))
            {
                region = alias.Length > 0 ? alias : null;
            }

            name = name[m.Length..];
        }

        var flags = new List<string>();
        var variants = new List<string>();
        var keep = new List<string>();
        foreach (Match f in BracketRegex().Matches(name))
        {
            var tag = f.Groups["t"].Value.Trim();
            flags.Add(tag);

            // "[1080p]", "(720p)": quality written as a tag still orders the streams of a channel
            foreach (Match q in QualityRegex().Matches(tag))
            {
                variants.Add(q.Value.ToUpperInvariant());
            }

            // "[4K]", "[[4K-Q]-Q]": a 4K/8K feed stays a channel of its own, as it does when 4K is part of the name
            var uhd = UhdRegex().Match(tag);
            if (uhd.Success)
            {
                keep.Add(uhd.Value.ToUpperInvariant());
            }
        }

        name = BracketRegex().Replace(name, " ");
        name = StrayBracketRegex().Replace(name, " ");
        if (keep.Count > 0)
        {
            name += " " + string.Join(' ', keep.Distinct());
        }

        name = SuperscriptRegex().Replace(name, " ");

        name = QualityRegex().Replace(name, x =>
        {
            variants.Add(x.Value.ToUpperInvariant());
            return " ";
        });
        name = NoiseRegex().Replace(name, " ");

        var display = Collapse(name);
        var rawKey = Collapse(PunctuationRegex().Replace(display, " ")).ToUpperInvariant();
        var key = Aliases.Canonical(rawKey);
        var variant = variants.Count > 0 ? string.Join('/', variants.Distinct()) : "default";
        return new Parsed(key, display.Length > 0 ? display : rawName.Trim(), variant, region, flags, rawKey);
    }

    /// <summary>
    /// Turns a provider category such as "XX| NEWS", "XX-TAG| SPORT" or "News" into a stable, readable
    /// Terebi category: "XX News", "XX Sport", "News".
    /// </summary>
    public static string CategoryLabel(string rawCategory, string? fallbackRegion = null)
    {
        var c = (rawCategory ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        string? region = null;
        var m = PrefixRegex().Match(c);
        if (m.Success)
        {
            region = m.Groups["p"].Length > 0 ? m.Groups["p"].Value.ToUpperInvariant().Split('-')[0] : null;
            c = c[m.Length..].Trim();
        }

        c = SuperscriptRegex().Replace(c, " ");
        c = QualityTagRegex().Replace(c, " ");
        c = Collapse(c);
        if (c.Length == 0)
        {
            c = "Other";
        }

        var words = c.Split(' ').Select(w => w.Length <= 3 || w.Any(char.IsDigit) || w.All(ch => !char.IsLetter(ch) || char.IsUpper(ch)) && w.Length <= 4 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        var label = string.Join(' ', words);
        region ??= fallbackRegion;
        return string.IsNullOrEmpty(region) ? label : region + " " + label;
    }

    /// <summary>Quality rank used to order streams of one channel (higher first).</summary>
    public static int QualityRank(string variant) => variant.ToUpperInvariant() switch
    {
        var v when v.Contains("RAW") => 90,
        var v when v.Contains("4K") || v.Contains("UHD") || v.Contains("2160") => 80,
        var v when v.Contains("FHD") || v.Contains("1080") || v.Contains("FULL HD") => 70,
        var v when v.Contains("HEVC") || v.Contains("H265") => 65,
        var v when v.Contains("HD") || v.Contains("720") => 60,
        "DEFAULT" => 50,
        var v when v.Contains("SD") || v.Contains("576") || v.Contains("480") => 30,
        var v when v.Contains("LQ") || v.Contains("360") => 10,
        _ => 40,
    };

    // Prefixes that are not ISO country codes; the first two letters of the value are used as the country, an empty value
    // means "no country". Users add the labels of their own providers in region-aliases.json.
    private static readonly Dictionary<string, string> BuiltInRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KOR"] = "KR",
        ["CHN"] = "CN",
        ["TAI"] = "TW",
    };

    private static Dictionary<string, string>? _userRegions;

    private static Dictionary<string, string> RegionAliases => _userRegions is { Count: > 0 } u ? new(BuiltInRegions.Concat(u.Where(kv => !BuiltInRegions.ContainsKey(kv.Key))), StringComparer.OrdinalIgnoreCase) : BuiltInRegions;

    /// <summary>Loads the user's region aliases (prefix to country, empty for none).</summary>
    public static void LoadRegionAliases(string path, Microsoft.Extensions.Logging.ILogger logger)
    {
        _userRegions = Storage.JsonFile.Load(path, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), logger);
    }

    private static string Collapse(string s) => WhitespaceRegex().Replace(s, " ").Trim();

    // "XX| Name", "XX-TAG| Name", "XX: Name", "XX - Name", "XX-[TAG]: Name", and brand prefixes without a region ("BRAND+: Name")
    [GeneratedRegex(@"^\s*(?<p>[A-Za-z0-9/\-]{2,8})\s*\|\s*|^\s*(?<p>[A-Za-z]{2,3})-\[[^\]]*\]\s*:\s*|^\s*(?<p>[A-Za-z0-9]{2,3})\s*[-:]\s+|^\s*[A-Za-z]{2,6}\+\s*:\s*")]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"\b(?:4K|8K|UHD)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UhdRegex();

    // leftovers of nested tags such as "[[4K-Q]-Q]"
    [GeneratedRegex(@"[\[\]]|-Q\b")]
    private static partial Regex StrayBracketRegex();

    // "[1080p]" and similar quality tags in provider category names
    [GeneratedRegex(@"\[\s*(?:\d{3,4}[pi]|4K|8K|UHD|FHD|HD|SD)[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTagRegex();

    [GeneratedRegex(@"\[(?<t>[^\]]*)\]|\((?<t>[^)]*)\)")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"[ᴴᴰᶠʰᵉᵛᶜᵁˢ⁴ᴷ]+")]
    private static partial Regex SuperscriptRegex();

    // 4K / UHD / 8K stay in the key on purpose: "CCTV-4K" or "Canal+ Sport 4K" are separate channels, not variants.
    [GeneratedRegex(@"\b(RAW|FHD|FULL HD|FULLHD|HD|SD|HQ|LQ|HEVC|H265|H\.265|HDR|1080P|1080I|720P|576P|576I|480P|360P|50FPS|60FPS|50 FPS|60 FPS)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRegex();

    [GeneratedRegex(@"\b(VIP|BACKUP|BACK UP|ALT|ALTERNATIVE|OPTION|OPT|LINK|SOURCE|SRC|SERVER|SRV|MIRROR)\s*\d*\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}\s+]")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
