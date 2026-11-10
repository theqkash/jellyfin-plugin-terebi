using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Terebi.Configuration;

/// <summary>
/// Only settings live here (Jellyfin serialises this to XML). Sources, the lineup and rules are
/// stored by the plugin itself as JSON under data/terebi because they are large and edited by tools.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets how often source catalogs (Xtream lists, M3U files) are re-downloaded, in minutes.</summary>
    public int SourceRefreshMinutes { get; set; } = 60;

    /// <summary>Gets or sets how often stream health is probed, in hours (0 = disabled).</summary>
    public int HealthCheckHours { get; set; } = 6;

    /// <summary>Gets or sets how many probes run at once against one source.</summary>
    public int HealthCheckConcurrencyPerSource { get; set; } = 2;

    /// <summary>Gets or sets a value indicating whether channels whose every stream failed the last probe are hidden from Jellyfin.</summary>
    public bool HideDeadChannels { get; set; } = true;

    /// <summary>Gets or sets after how many consecutive failed probes a stream counts as dead.</summary>
    public int DeadAfterFailures { get; set; } = 2;

    /// <summary>Gets or sets the ring buffer size per live stream, in MiB.</summary>
    public int StreamBufferMiB { get; set; } = 16;

    /// <summary>Gets or sets how long to wait for the first bytes before failing over to the next stream, in seconds.</summary>
    public int StreamStartTimeoutSeconds { get; set; } = 12;

    /// <summary>Gets or sets how long a running stream may stall before failing over, in seconds.</summary>
    public int StreamStallTimeoutSeconds { get; set; } = 20;

    /// <summary>Gets or sets how often EPG files are re-downloaded, in hours.</summary>
    public int EpgRefreshHours { get; set; } = 12;

    /// <summary>Gets or sets the user agent used for all provider requests.</summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    /// <summary>Gets or sets the first number used for automatic per-category blocks (channels not covered by a rule).</summary>
    public int AutoBlockStart { get; set; } = 12000;

    /// <summary>Gets or sets the size of an automatic per-category block; a category with more channels gets a multiple.</summary>
    public int AutoBlockSize { get; set; } = 100;

    /// <summary>Gets or sets a value indicating whether the "Terebi" folder tree (categories with channels) is offered under Channels.</summary>
    public bool EnableChannelFolders { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether Jellyfin's guide is refreshed automatically when channels are added or removed.</summary>
    public bool AutoRefreshGuide { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a category filter is added to the web client's Live TV pages (needs the File Transformation plugin).</summary>
    public bool EnableWebCategoryFilter { get; set; } = true;

    /// <summary>Gets or sets the secret that protects the raw stream endpoint (generated on first start).</summary>
    public string StreamKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether channel names follow the primary stream's current provider name.</summary>
    public bool FollowProviderNamesByDefault { get; set; } = false;

    /// <summary>Gets or sets a value indicating whether logos are looked up in the iptv-org channel database by name and country.</summary>
    public bool EnableLogoIndex { get; set; } = true;

    /// <summary>Gets or sets the priority of index logos against source logos (a source with a higher logo priority wins).</summary>
    public int LogoIndexPriority { get; set; } = 5;

    /// <summary>Gets or sets how many days an unreferenced cached logo is kept.</summary>
    public int LogoCacheDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets a value indicating whether sources limited to one connection are probed by the health check.
    /// Off by default: every probe would occupy the account's only slot and viewers would get "too many connections".
    /// Their streams are still graded by real playback attempts.
    /// </summary>
    public bool ProbeSingleConnectionSources { get; set; } = false;

    /// <summary>Gets or sets how long an upstream connection is kept after the last viewer left, so a quick reconnect (probe then play, player restart) reuses it.</summary>
    public int StreamLingerSeconds { get; set; } = 10;

    /// <summary>Gets or sets after how many seconds without anybody reading from a stream it is closed (0 = off). Frees provider connections left behind by clients that vanished.</summary>
    public int WatchdogIdleSeconds { get; set; } = 60;

    /// <summary>Gets or sets a value indicating whether a stream opened through Jellyfin is closed when no Jellyfin session plays its channel any more (after a short grace period).</summary>
    public bool WatchdogRequireSession { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether channels without guide data get the XMLTV channel with the same name mapped automatically.</summary>
    public bool AutoMapEpgByName { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether folder-tree item names carry the programme on air ("0123  TVN 24  ·  Fakty"), refreshed every 10 minutes.</summary>
    public bool ShowNowPlayingInFolderNames { get; set; } = true;
}
