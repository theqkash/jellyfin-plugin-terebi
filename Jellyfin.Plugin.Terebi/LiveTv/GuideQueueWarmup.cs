using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Terebi.LiveTv;

/// <summary>Instantiates the guide refresh queue at startup so the health checker can reach it from the first run.</summary>
public sealed class GuideQueueWarmup : IHostedService
{
    public GuideQueueWarmup(GuideRefreshQueue queue)
    {
        _ = queue;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
