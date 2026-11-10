using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Terebi.Channels;

public enum NameMode
{
    /// <summary>The name is what the user typed (or what was captured at creation).</summary>
    Fixed,

    /// <summary>The name follows the primary stream's current provider name (event channels and PPV slots).</summary>
    FollowPrimary,
}

/// <summary>A reference from a logical channel to one stream in the catalog.</summary>
public sealed class StreamRef
{
    /// <summary>Gets or sets "sourceId:key".</summary>
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    /// <summary>Gets or sets the order in which streams are tried (lower first).</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the provider name at the time of matching; lets the matcher re-find a stream whose id changed.</summary>
    [JsonPropertyName("matchedName")]
    public string? MatchedName { get; set; }

    [JsonPropertyName("matchedCategory")]
    public string? MatchedCategory { get; set; }

    [JsonPropertyName("variant")]
    public string? Variant { get; set; }

    [JsonIgnore]
    public string SourceId => Ref.Split(':', 2)[0];
}

/// <summary>
/// What the user sees in Jellyfin: one channel that can be fed by several streams.
/// </summary>
public sealed class LogicalChannel
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("nameMode")]
    public NameMode NameMode { get; set; } = NameMode.Fixed;

    /// <summary>Gets or sets the normalised key used to match new streams automatically.</summary>
    [JsonPropertyName("matchKey")]
    public string MatchKey { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public int Number { get; set; }

    /// <summary>Gets or sets the Terebi category (what the rules assigned or the user chose), shown as a tag.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    /// <summary>Gets or sets where the logo came from: "index" (iptv-org) or a source id.</summary>
    [JsonPropertyName("logoOrigin")]
    public string? LogoOrigin { get; set; }

    /// <summary>Gets or sets XMLTV channel ids, first match wins.</summary>
    [JsonPropertyName("epgIds")]
    public List<string> EpgIds { get; set; } = [];

    [JsonPropertyName("streams")]
    public List<StreamRef> Streams { get; set; } = [];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the channel was created by rules (true) or by hand (false).</summary>
    [JsonPropertyName("auto")]
    public bool Auto { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the user edited the channel; auto-matching then leaves name/number/category alone.</summary>
    [JsonPropertyName("pinned")]
    public bool Pinned { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("region")]
    public string? Region { get; set; }
}

/// <summary>
/// A rule that assigns streams to a Terebi category and number block, and decides whether unmatched streams
/// become channels automatically. Rules are evaluated in order; first match wins.
/// </summary>
public sealed class CategoryRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets which sources the rule applies to (empty = all).</summary>
    [JsonPropertyName("sourceIds")]
    public List<string> SourceIds { get; set; } = [];

    /// <summary>Gets or sets a regex matched against the provider category / group-title.</summary>
    [JsonPropertyName("categoryPattern")]
    public string? CategoryPattern { get; set; }

    /// <summary>Gets or sets a regex matched against the raw stream name.</summary>
    [JsonPropertyName("namePattern")]
    public string? NamePattern { get; set; }

    /// <summary>Gets or sets the Terebi category assigned to matching channels.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets the first channel number of this block (channels get consecutive numbers).</summary>
    [JsonPropertyName("numberStart")]
    public int NumberStart { get; set; }

    /// <summary>Gets or sets a value indicating whether streams matching the rule create channels automatically.</summary>
    [JsonPropertyName("autoAdd")]
    public bool AutoAdd { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether channels from this rule follow provider names (event channels).</summary>
    [JsonPropertyName("followProviderName")]
    public bool FollowProviderName { get; set; }

    /// <summary>Gets or sets a value indicating whether matching streams are ignored entirely (blacklist rule).</summary>
    [JsonPropertyName("exclude")]
    public bool Exclude { get; set; }
}

public sealed class Lineup
{
    [JsonPropertyName("channels")]
    public List<LogicalChannel> Channels { get; set; } = [];

    /// <summary>Gets or sets the first number of the automatic block for each category (allocated on first use, then stable).</summary>
    [JsonPropertyName("categoryBlocks")]
    public Dictionary<string, int> CategoryBlocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("rules")]
    public List<CategoryRule> Rules { get; set; } = [];

    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset UpdatedUtc { get; set; }
}
