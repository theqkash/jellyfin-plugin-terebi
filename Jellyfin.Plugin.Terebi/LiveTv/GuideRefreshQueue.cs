using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.LiveTv;

/// <summary>
/// Runs Jellyfin's guide refresh at most once at a time. Requests that arrive while one is running are
/// folded into a single follow-up run, so a burst of changes (rebuild, logos, health) never produces
/// overlapping refreshes that cancel each other.
/// </summary>
public sealed class GuideRefreshQueue
{
    private readonly IGuideManager _guide;
    private readonly ILogger<GuideRefreshQueue> _logger;
    private readonly object _lock = new();
    private bool _running;
    private bool _pending;

    public GuideRefreshQueue(IGuideManager guide, Health.HealthChecker health, ILogger<GuideRefreshQueue> logger)
    {
        _guide = guide;
        _logger = logger;
        health.GuideQueue = this;
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    /// <summary>Asks for a guide refresh; returns immediately.</summary>
    public void Request(string reason)
    {
        lock (_lock)
        {
            if (_running)
            {
                _pending = true;
                _logger.LogInformation("Terebi: guide refresh queued ({Reason})", reason);
                return;
            }

            _running = true;
        }

        _logger.LogInformation("Terebi: asking Jellyfin to refresh its guide ({Reason})", reason);
        _ = Task.Run(() => RunLoopAsync());
    }

    private async Task RunLoopAsync()
    {
        while (true)
        {
            try
            {
                await _guide.RefreshGuide(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Terebi: guide refresh finished");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Terebi: guide refresh failed");
            }

            lock (_lock)
            {
                if (!_pending)
                {
                    _running = false;
                    return;
                }

                _pending = false;
            }
        }
    }
}
