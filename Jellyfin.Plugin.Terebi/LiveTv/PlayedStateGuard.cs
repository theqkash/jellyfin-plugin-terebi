using Jellyfin.Plugin.Terebi.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.LiveTv;

/// <summary>
/// Keeps live channels in the folder tree from being marked as watched. Jellyfin has no runtime for a live
/// item, and an item without a runtime is flagged "played" the moment playback stops; a TV channel never is.
/// </summary>
public sealed class PlayedStateGuard : IHostedService
{
    private readonly IUserDataManager _userData;
    private readonly IUserManager _users;
    private readonly LineupStore _store;
    private readonly ILogger<PlayedStateGuard> _logger;

    [ThreadStatic]
    private static bool _reentrant;

    public PlayedStateGuard(IUserDataManager userData, IUserManager users, LineupStore store, ILogger<PlayedStateGuard> logger)
    {
        _userData = userData;
        _users = users;
        _store = store;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (_reentrant || e.Item is not BaseItem item || e.UserData is null || !e.UserData.Played)
        {
            return;
        }

        if (item.SourceType != SourceType.Channel || item.ExternalId is not { } ext || !ext.StartsWith("ch:", StringComparison.Ordinal)
            || !Guid.TryParseExact(ext[3..], "N", out var channelId) || _store.GetChannel(channelId) is null)
        {
            return;
        }

        try
        {
            _reentrant = true;
            e.UserData.Played = false;
            e.UserData.PlaybackPositionTicks = 0;
            var user = _users.GetUserById(e.UserId);
            if (user is null)
            {
                return;
            }

            _userData.SaveUserData(user, item, e.UserData, UserDataSaveReason.UpdateUserData, CancellationToken.None);
            _logger.LogDebug("Terebi: cleared played flag on live channel item {Name}", item.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _logger.LogDebug(ex, "Terebi: could not clear the played flag on {Name}", item.Name);
        }
        finally
        {
            _reentrant = false;
        }
    }
}
