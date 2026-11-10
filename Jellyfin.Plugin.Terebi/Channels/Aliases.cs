namespace Jellyfin.Plugin.Terebi.Channels;

/// <summary>
/// Known alternative names that should merge into one channel even though the normalised keys differ:
/// Japanese call signs used by free lists, "NHK GENERAL TV" versus "NHK", and similar. Keys are the
/// normalised form produced by <see cref="NameNormalizer"/>. Users can add their own in aliases.json.
/// </summary>
public static class Aliases
{
    private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JOAX DTV"] = "NIPPON TV",
        ["NTV"] = "NIPPON TV",
        ["JOCX DTV"] = "FUJI TELEVISION",
        ["FUJI TV"] = "FUJI TELEVISION",
        ["JOEX DTV"] = "TV ASAHI",
        ["JORX DTV"] = "TBS TELEVISION",
        ["TBS"] = "TBS TELEVISION",
        ["JOTX DTV"] = "TV TOKYO",
        ["JOMX DTV"] = "TOKYO MX",
        ["J SPORTS 1 SPORTS"] = "J SPORTS 1",
        ["J SPORTS 2 SPORTS"] = "J SPORTS 2",
        ["J SPORTS 3 SPORTS"] = "J SPORTS 3",
        ["J SPORTS 4 SPORTS"] = "J SPORTS 4",
        ["WOWOW CINEMA MOVIES"] = "WOWOW CINEMA",
        ["SCIFI UNIVERSAL"] = "SCI FI",
        ["STAR CHANNEL 1"] = "STAR CHANNEL",
        ["JOAK DTV"] = "NHK",
        ["NHK GENERAL TV"] = "NHK",
        ["NHK G"] = "NHK",
        ["NHK EDUCATIONAL TV"] = "NHK E",
        ["NHK ETV"] = "NHK E",
        ["JOAB DTV"] = "NHK E",
        ["NHK WORLD JAPAN"] = "NHK WORLD",
        ["NHK WORLD TV"] = "NHK WORLD",
    };

    private static Dictionary<string, string>? _user;

    public static void LoadUser(string path, Microsoft.Extensions.Logging.ILogger logger)
    {
        _user = Storage.JsonFile.Load(path, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), logger);
    }

    /// <summary>Maps a normalised key to its canonical key when an alias exists.</summary>
    public static string Canonical(string key)
    {
        if (_user is not null && _user.TryGetValue(key, out var u) && !string.IsNullOrWhiteSpace(u))
        {
            return u.Trim().ToUpperInvariant();
        }

        return BuiltIn.TryGetValue(key, out var v) ? v : key;
    }
}
