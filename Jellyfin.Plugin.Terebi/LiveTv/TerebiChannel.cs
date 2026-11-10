using System.Globalization;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Epg;
using Jellyfin.Plugin.Terebi.Health;
using Jellyfin.Plugin.Terebi.Logos;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Terebi.LiveTv;

/// <summary>
/// A browsable folder tree ("Terebi" under Channels): one folder per category, channels inside, so a viewer
/// on any client can pick a category instead of scrolling a thousand numbers. Each entry plays through
/// Terebi's own restream endpoint, which shares the same failover stream the Live TV guide uses.
/// </summary>
public sealed class TerebiChannel : IChannel, IHasCacheKey, IDisableMediaSourceDisplay
{
    private readonly LineupStore _store;
    private readonly HealthChecker _health;
    private readonly EpgService _epg;
    private readonly LogoCache _logos;
    private readonly CategoryImages _categoryImages;
    private readonly IServerApplicationHost _appHost;

    public TerebiChannel(LineupStore store, HealthChecker health, EpgService epg, LogoCache logos, CategoryImages categoryImages, IServerApplicationHost appHost)
    {
        _store = store;
        _health = health;
        _epg = epg;
        _logos = logos;
        _categoryImages = categoryImages;
        _appHost = appHost;
    }

    public string Name => "Terebi";

    public string Description => "Live TV channels by category";

    public string DataVersion => _store.Lineup.UpdatedUtc.UtcTicks.ToString(CultureInfo.InvariantCulture);

    public string HomePageUrl => string.Empty;

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public string GetCacheKey(string? userId) => DataVersion;

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes = [ChannelMediaContentType.Clip],
        MediaTypes = [ChannelMediaType.Video],
        MaxPageSize = 500,
        DefaultSortFields = [ChannelItemSortField.Name],
        SupportsSortOrderToggle = false,
        SupportsContentDownloading = false,
        AutoRefreshLevels = 1,
    };

    public bool IsEnabledFor(string userId) => Plugin.Instance?.Configuration.EnableChannelFolders ?? true;

    public IEnumerable<ImageType> GetSupportedChannelImages() => [];

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) => Task.FromResult(new DynamicImageResponse { HasImage = false });

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var hideDead = Plugin.Instance?.Configuration.HideDeadChannels ?? true;
        var channels = _store.Lineup.Channels
            .Where(c => c.Enabled && c.Streams.Any(s => s.Enabled))
            .Where(c => !hideDead || !c.Streams.Where(s => s.Enabled).All(s => _health.Get(s.Ref).IsDead))
            .ToList();

        if (string.IsNullOrEmpty(query.FolderId))
        {
            // Root: one folder per category, ordered by the category's first channel number.
            var folders = channels
                .GroupBy(c => string.IsNullOrWhiteSpace(c.Category) ? "Other" : c.Category, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Min(c => c.Number))
                .Select((g, i) => new ChannelItemInfo
                {
                    Id = "cat:" + g.Key,
                    Name = g.Key,
                    Overview = g.Count().ToString(CultureInfo.InvariantCulture) + " channels",
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container,
                    ImageUrl = _categoryImages.UrlFor(g.Key, g.OrderBy(c => c.Number).ToList()),
                    IndexNumber = i + 1,
                })
                .ToList();
            return Task.FromResult(new ChannelItemResult { Items = folders, TotalRecordCount = folders.Count });
        }

        if (!query.FolderId.StartsWith("cat:", StringComparison.Ordinal))
        {
            return Task.FromResult(new ChannelItemResult());
        }

        var category = query.FolderId[4..];
        var now = DateTimeOffset.UtcNow;
        var items = new List<ChannelItemInfo>();
        foreach (var c in channels.Where(c => string.Equals(string.IsNullOrWhiteSpace(c.Category) ? "Other" : c.Category, category, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Number))
        {
            var (current, next) = _epg.GetNowNext(c, now);
            items.Add(new ChannelItemInfo
            {
                Id = "ch:" + c.Id.ToString("N"),
                Name = NowPlayingText.Name(c, current),
                OriginalTitle = c.Name,
                Overview = NowPlayingText.Overview(current, next),
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                IsLiveStream = true,
                ImageUrl = _logos.LocalUrlOrPlaceholder(c.Logo),
                Tags = string.IsNullOrWhiteSpace(c.Category) ? [] : [c.Category],
                IndexNumber = c.Number,
                // Changing the etag makes Jellyfin re-save the item (name, number, logo or key changed).
                Etag = string.Join(':', "v4", c.Number, c.Name, _logos.LocalUrlOrPlaceholder(c.Logo)),
                MediaSources = [BuildMediaSource(c)],
            });
        }

        return Task.FromResult(new ChannelItemResult { Items = items, TotalRecordCount = items.Count });
    }

    /// <summary>Display name of a folder-tree item without programme: zero-padded number so clients that sort by name keep the block order.</summary>
    public static string ItemName(LogicalChannel c) => NowPlayingText.Name(c, null);

    /// <summary>Media source pointing at Terebi's own restream endpoint (server-local URL, ffmpeg reads it).</summary>
    public MediaSourceInfo BuildMediaSource(LogicalChannel c)
    {
        var key = Plugin.Instance?.Configuration.StreamKey ?? string.Empty;
        var path = $"{_appHost.GetApiUrlForLocalAccess()}/Terebi/Stream/{c.Id:N}.ts?key={Uri.EscapeDataString(key)}";
        return new MediaSourceInfo
        {
            // Must not parse as a GUID: Jellyfin drops GUID-shaped source ids it cannot resolve to an item.
            Id = "terebi-" + c.Id.ToString("N"),
            Name = c.Name,
            Path = path,
            Protocol = MediaProtocol.Http,
            Container = "ts",
            IsInfiniteStream = true,
            IsRemote = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = true,
            RequiresOpening = false,
            IgnoreDts = true,
            IgnoreIndex = true,
            AnalyzeDurationMs = 3000,
            // Placeholder streams: tells Jellyfin the source is video+audio without a probe; ffmpeg fills in the rest.
            MediaStreams =
            [
                // No IsInterlaced here: the web client refuses interlaced video and would transcode every channel.
                new MediaStream { Type = MediaStreamType.Video, Index = -1 },
                new MediaStream { Type = MediaStreamType.Audio, Index = -1 },
            ],
        };
    }
}
