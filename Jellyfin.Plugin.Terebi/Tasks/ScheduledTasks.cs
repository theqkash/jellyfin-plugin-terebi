using Jellyfin.Plugin.Terebi.Epg;
using Jellyfin.Plugin.Terebi.Health;
using Jellyfin.Plugin.Terebi.Sources;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Terebi.Tasks;

/// <summary>Re-downloads sources and rebuilds the lineup (also picks up renamed event channels).</summary>
public sealed class RefreshSourcesTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly SourceRefresher _refresher;

    public RefreshSourcesTask(SourceRefresher refresher)
    {
        _refresher = refresher;
    }

    public string Name => "Terebi: refresh sources";

    public string Key => "TerebiRefreshSources";

    public string Description => "Downloads Xtream and M3U channel lists and rebuilds the merged channel lineup.";

    public string Category => "Terebi";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(Math.Max(10, Plugin.Instance?.Configuration.SourceRefreshMinutes ?? 60)).Ticks,
        };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => _refresher.RefreshAllAsync(progress, cancellationToken);
}

/// <summary>Probes every stream and marks dead ones.</summary>
public sealed class HealthCheckTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly HealthChecker _health;

    public HealthCheckTask(HealthChecker health)
    {
        _health = health;
    }

    public string Name => "Terebi: check stream health";

    public string Key => "TerebiHealthCheck";

    public string Description => "Tries to start every stream and hides channels whose streams are all dead.";

    public string Category => "Terebi";

    public bool IsHidden => false;

    public bool IsEnabled => (Plugin.Instance?.Configuration.HealthCheckHours ?? 6) > 0;

    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(Math.Max(1, Plugin.Instance?.Configuration.HealthCheckHours ?? 6)).Ticks,
        };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => _health.RunAsync(progress, cancellationToken);
}

/// <summary>Downloads XMLTV files.</summary>
public sealed class RefreshEpgTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly EpgService _epg;

    public RefreshEpgTask(EpgService epg)
    {
        _epg = epg;
    }

    public string Name => "Terebi: refresh EPG";

    public string Key => "TerebiRefreshEpg";

    public string Description => "Downloads the configured XMLTV guides.";

    public string Category => "Terebi";

    public bool IsHidden => false;

    public bool IsEnabled => true;

    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(Math.Max(1, Plugin.Instance?.Configuration.EpgRefreshHours ?? 12)).Ticks,
        };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => _epg.RefreshAsync(progress, cancellationToken);
}
