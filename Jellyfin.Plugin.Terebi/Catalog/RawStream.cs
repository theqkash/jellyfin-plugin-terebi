using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Terebi.Catalog;

/// <summary>
/// One stream exactly as a source published it. Key is stable per source (Xtream stream_id or a hash of the URL).
/// </summary>
public sealed class RawStream
{
    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    /// <summary>Gets or sets the provider's EPG id (tvg-id / epg_channel_id).</summary>
    [JsonPropertyName("epgId")]
    public string? EpgId { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider's own channel number when it publishes one.</summary>
    [JsonPropertyName("number")]
    public int? Number { get; set; }

    [JsonPropertyName("catchupDays")]
    public int CatchupDays { get; set; }

    [JsonPropertyName("firstSeenUtc")]
    public DateTimeOffset FirstSeenUtc { get; set; }

    [JsonPropertyName("lastSeenUtc")]
    public DateTimeOffset LastSeenUtc { get; set; }

    /// <summary>Gets a composite id "sourceId:key" used to reference this stream from the lineup.</summary>
    [JsonIgnore]
    public string Ref => SourceId + ":" + Key;

    [JsonIgnore]
    public bool IsDash => Url.Contains(".mpd", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsHls => Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
}
