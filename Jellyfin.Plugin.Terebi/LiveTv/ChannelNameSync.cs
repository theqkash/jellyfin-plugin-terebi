using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Terebi.Channels;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.LiveTv;

/// <summary>
/// Pushes changed channel names, numbers and logos straight into Jellyfin's LiveTvChannel items, so a
/// provider renaming an event slot ("EVENT 12" becoming "EVENT 12 | Team A vs Team B") shows up
/// within one source refresh instead of waiting for the next full guide refresh.
/// </summary>
public sealed class ChannelNameSync
{
    private readonly ILibraryManager _libraryManager;
    private readonly IChannelManager _channelManager;
    private readonly Logos.LogoCache _logos;
    private readonly Logos.CategoryImages _categoryImages;
    private readonly Epg.EpgService _epg;
    private readonly LineupStore _store;
    private readonly ILogger<ChannelNameSync> _logger;

    public ChannelNameSync(ILibraryManager libraryManager, IChannelManager channelManager, Logos.LogoCache logos, Logos.CategoryImages categoryImages, Epg.EpgService epg, LineupStore store, ILogger<ChannelNameSync> logger)
    {
        _libraryManager = libraryManager;
        _channelManager = channelManager;
        _logos = logos;
        _categoryImages = categoryImages;
        _epg = epg;
        _store = store;
        _logger = logger;
    }

    /// <summary>Jellyfin's entity id for the "Terebi" channel (folder tree root).</summary>
    private async Task<Guid?> TerebiChannelIdAsync()
    {
        try
        {
            var channels = await _channelManager.GetChannelsInternalAsync(new MediaBrowser.Model.Channels.ChannelQuery()).ConfigureAwait(false);
            var mine = channels.Items.FirstOrDefault(c => string.Equals(c.Name, "Terebi", StringComparison.Ordinal));
            if (mine is not null)
            {
                return mine.Id;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(ex, "Terebi: channel lookup failed");
        }

        // Same derivation Jellyfin uses for channel entities.
        return _libraryManager.GetNewItemId("Channel Terebi", typeof(MediaBrowser.Controller.Channels.Channel));
    }

    public async Task<int> SyncAsync(CancellationToken cancellationToken)
    {
        var byExternalId = _store.Lineup.Channels.ToDictionary(c => TerebiLiveTvService.ChannelIdOf(c), StringComparer.OrdinalIgnoreCase);
        var items = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.LiveTvChannel] });
        var updated = 0;
        foreach (var item in items.OfType<LiveTvChannel>())
        {
            if (string.IsNullOrEmpty(item.ExternalId) || !byExternalId.TryGetValue(item.ExternalId, out var channel))
            {
                continue;
            }

            var changed = false;
            if (!string.Equals(item.Name, channel.Name, StringComparison.Ordinal))
            {
                item.Name = channel.Name;
                changed = true;
            }

            var number = channel.Number > 0 ? channel.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            if (!string.Equals(item.Number, number, StringComparison.Ordinal))
            {
                item.Number = number;
                changed = true;
            }

            if (!changed)
            {
                continue;
            }

            try
            {
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                updated++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                _logger.LogWarning(ex, "Terebi: could not update Jellyfin channel {Name}", channel.Name);
            }
        }

        updated += await SyncFolderItemsAsync(cancellationToken).ConfigureAwait(false);

        if (updated > 0)
        {
            _logger.LogInformation("Terebi: pushed {Count} channel name/number changes to Jellyfin", updated);
        }

        return updated;
    }

    /// <summary>
    /// Folder-tree items: Jellyfin sets a channel item's name and image only when it creates it, so renamed or
    /// renumbered channels would keep their first look forever. Names also carry the current programme, so this
    /// runs every few minutes as well.
    /// </summary>
    public async Task<int> SyncFolderItemsAsync(CancellationToken cancellationToken)
    {
        var updated = 0;
        var now = DateTimeOffset.UtcNow;
        var byItemExternalId = _store.Lineup.Channels.ToDictionary(c => "ch:" + c.Id.ToString("N"), StringComparer.Ordinal);
        var channelId = await TerebiChannelIdAsync().ConfigureAwait(false);
        var folderItems = channelId is null
            ? []
            : _libraryManager.GetItemList(new InternalItemsQuery { ChannelIds = [channelId.Value], IncludeItemTypes = [BaseItemKind.Video] });
        var folderUpdated = 0;
        foreach (var item in folderItems)
        {
            if (string.IsNullOrEmpty(item.ExternalId) || !byItemExternalId.TryGetValue(item.ExternalId, out var channel))
            {
                continue;
            }

            var (current, next) = _epg.GetNowNext(channel, now);
            var wantedName = NowPlayingText.Name(channel, current);
            var wantedOverview = NowPlayingText.Overview(current, next);
            var changed = false;
            if (!string.Equals(item.Name, wantedName, StringComparison.Ordinal))
            {
                item.Name = wantedName;
                changed = true;
            }

            if (!string.Equals(item.Overview, wantedOverview, StringComparison.Ordinal))
            {
                item.Overview = wantedOverview;
                changed = true;
            }

            if (item.IndexNumber != channel.Number)
            {
                item.IndexNumber = channel.Number;
                changed = true;
            }

            if (!string.Equals(item.OriginalTitle, channel.Name, StringComparison.Ordinal))
            {
                item.OriginalTitle = channel.Name;
                changed = true;
            }

            // Jellyfin sets a channel item's image only when the item is created; swap it ourselves when the logo changed.
            var wantedImage = _logos.LocalPathOrPlaceholder(channel.Logo);
            var currentImage = item.GetImageInfo(ImageType.Primary, 0)?.Path;
            var imageChanged = !string.Equals(currentImage, wantedImage, StringComparison.Ordinal);
            if (imageChanged)
            {
                item.SetImagePath(ImageType.Primary, wantedImage);
                changed = true;
            }

            if (!changed)
            {
                continue;
            }

            try
            {
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), imageChanged ? ItemUpdateType.ImageUpdate : ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                updated++;
                folderUpdated++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                _logger.LogWarning(ex, "Terebi: could not update folder item {Name}", channel.Name);
            }
        }

        _logger.LogInformation("Terebi: folder tree has {Count} items in Jellyfin, {Updated} renamed", folderItems.Count, folderUpdated);

        // Category folders: Jellyfin keeps the image they were created with, so swap in the current tile.
        if (channelId is not null)
        {
            var hideDead = Plugin.Instance?.Configuration.HideDeadChannels ?? true;
            var byCategory = _store.Lineup.Channels
                .Where(c => c.Enabled && c.Streams.Any(s => s.Enabled))
                .GroupBy(c => string.IsNullOrWhiteSpace(c.Category) ? "Other" : c.Category, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => "cat:" + g.Key, g => g.OrderBy(c => c.Number).ToList(), StringComparer.Ordinal);
            var folders = _libraryManager.GetItemList(new InternalItemsQuery { ChannelIds = [channelId.Value], IncludeItemTypes = [BaseItemKind.Folder] });
            var foldersUpdated = 0;
            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder.ExternalId) || !byCategory.TryGetValue(folder.ExternalId, out var members))
                {
                    continue;
                }

                var wanted = _categoryImages.PathFor(folder.ExternalId[4..], members);
                if (wanted is null || string.Equals(folder.GetImageInfo(ImageType.Primary, 0)?.Path, wanted, StringComparison.Ordinal))
                {
                    continue;
                }

                folder.SetImagePath(ImageType.Primary, wanted);
                try
                {
                    await _libraryManager.UpdateItemAsync(folder, folder.GetParent(), ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                    foldersUpdated++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    _logger.LogWarning(ex, "Terebi: could not update category folder {Name}", folder.Name);
                }
            }

            if (foldersUpdated > 0)
            {
                _logger.LogInformation("Terebi: {Count} category folder tiles updated", foldersUpdated);
            }
        }

        return updated;
    }

}
