using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Terebi.Sources;

public enum SourceType
{
    Xtream,
    M3u,
}

/// <summary>
/// A provider of streams: an Xtream account or an M3U playlist (URL or file).
/// </summary>
public sealed class SourceDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public SourceType Type { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the preference order when several sources carry the same channel (lower = tried first).</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 100;

    /// <summary>Gets or sets the Xtream base URL (http://host:port/) or the M3U URL / file path.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>Gets or sets the maximum simultaneous streams the provider allows (0 = unknown/unlimited).</summary>
    [JsonPropertyName("maxConnections")]
    public int MaxConnections { get; set; }

    /// <summary>Gets or sets the preferred Xtream output: "ts" or "m3u8".</summary>
    [JsonPropertyName("xtreamOutput")]
    public string XtreamOutput { get; set; } = "ts";

    /// <summary>Gets or sets a regex; only provider categories (Xtream) or group-titles (M3U) matching it are imported. Empty = all.</summary>
    [JsonPropertyName("includeCategories")]
    public string? IncludeCategories { get; set; }

    [JsonPropertyName("excludeCategories")]
    public string? ExcludeCategories { get; set; }

    /// <summary>Gets or sets a value indicating whether the account's own XMLTV feed (xmltv.php) is used for Xtream sources.</summary>
    [JsonPropertyName("useProviderEpg")]
    public bool UseProviderEpg { get; set; } = true;

    /// <summary>Gets or sets XMLTV URLs whose channel ids apply to this source's tvg-id / epg_channel_id values.</summary>
    [JsonPropertyName("epgUrls")]
    public List<string> EpgUrls { get; set; } = [];

    /// <summary>Gets or sets the two-letter country of the channels in this source (used for logo, EPG and identity matching when names carry no region).</summary>
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    /// <summary>Gets or sets XMLTV URLs announced by the playlist itself (url-tvg header); used when no EPG URLs are configured.</summary>
    [JsonPropertyName("discoveredEpgUrls")]
    public List<string> DiscoveredEpgUrls { get; set; } = [];

    /// <summary>
    /// Gets or sets a fixed Terebi category for every channel created from this source (e.g. "JP Free").
    /// Empty = derive it from the provider category / group-title.
    /// </summary>
    [JsonPropertyName("categoryLabel")]
    public string? CategoryLabel { get; set; }

    /// <summary>
    /// Gets or sets how much this source's logos are trusted when a channel has streams from several sources.
    /// Higher wins; the iptv-org logo index competes with its own priority (plugin settings).
    /// </summary>
    [JsonPropertyName("logoPriority")]
    public int LogoPriority { get; set; }

    /// <summary>Gets or sets a value indicating whether streams from this source are auto-added as new channels when unmatched.</summary>
    [JsonPropertyName("autoAddChannels")]
    public bool AutoAddChannels { get; set; } = true;

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("lastRefreshUtc")]
    public DateTimeOffset? LastRefreshUtc { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("streamCount")]
    public int StreamCount { get; set; }
}
