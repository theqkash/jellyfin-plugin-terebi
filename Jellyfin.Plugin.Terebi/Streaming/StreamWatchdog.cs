using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Streaming;

/// <summary>
/// Closes upstream connections nobody is watching. Two signals: no consumer has read from the buffer for
/// a while (the client vanished without closing), and, for streams opened through Jellyfin, no Jellyfin
/// session plays the channel any more. Matters most for provider accounts with a single connection,
/// where a forgotten stream blocks every other viewer.
/// </summary>
public sealed class StreamWatchdog : IHostedService, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SessionGrace = TimeSpan.FromSeconds(90);
    private readonly StreamManager _streams;
    private readonly ISessionManager _sessions;
    private readonly ILogger<StreamWatchdog> _logger;
    private Timer? _timer;
    private int _running;

    public StreamWatchdog(StreamManager streams, ISessionManager sessions, ILogger<StreamWatchdog> logger)
    {
        _streams = streams;
        _sessions = sessions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => _ = TickAsync(), null, Interval, Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>How many Jellyfin sessions are currently playing the channel (Live TV item or folder item).</summary>
    public int WatchingSessions(Guid channelId)
    {
        var liveId = channelId.ToString("N");
        var itemExternalId = "ch:" + liveId;
        var count = 0;
        foreach (var session in _sessions.Sessions)
        {
            var item = session.FullNowPlayingItem;
            if (item is null)
            {
                continue;
            }

            if (string.Equals(item.ExternalId, liveId, StringComparison.OrdinalIgnoreCase) || string.Equals(item.ExternalId, itemExternalId, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var cfg = Plugin.Instance?.Configuration;
            var idleSeconds = cfg?.WatchdogIdleSeconds ?? 60;
            var requireSession = cfg?.WatchdogRequireSession ?? true;
            var now = DateTimeOffset.UtcNow;
            foreach (var stream in _streams.Open)
            {
                if (stream.ConsumerCount <= 0)
                {
                    continue; // lingering, the linger timer handles it
                }

                var sinceRead = now - stream.LastReadUtc;
                if (idleSeconds > 0 && sinceRead > TimeSpan.FromSeconds(idleSeconds))
                {
                    await _streams.ForceCloseAsync(stream, $"nobody read from it for {(int)sinceRead.TotalSeconds}s").ConfigureAwait(false);
                    continue;
                }

                if (requireSession && stream.ExternalConsumers == 0 && now - stream.OpenedUtc > SessionGrace && WatchingSessions(stream.ChannelId) == 0)
                {
                    await _streams.ForceCloseAsync(stream, "no Jellyfin session is playing this channel").ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _logger.LogDebug(ex, "Terebi: watchdog tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
