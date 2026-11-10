using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.LiveTv;

/// <summary>Keeps the current programme in folder-tree item names fresh (every 10 minutes).</summary>
public sealed class NowPlayingSync : IHostedService, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private readonly ChannelNameSync _sync;
    private readonly ILogger<NowPlayingSync> _logger;
    private Timer? _timer;
    private int _running;

    public NowPlayingSync(ChannelNameSync sync, ILogger<NowPlayingSync> logger)
    {
        _sync = sync;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMinutes(2), Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private async Task TickAsync()
    {
        if (!(Plugin.Instance?.Configuration.ShowNowPlayingInFolderNames ?? true) || Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var n = await _sync.SyncFolderItemsAsync(CancellationToken.None).ConfigureAwait(false);
            if (n > 0)
            {
                _logger.LogDebug("Terebi: now playing refreshed on {Count} folder items", n);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Terebi: now playing refresh failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
