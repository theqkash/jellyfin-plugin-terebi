using Jellyfin.Plugin.Terebi.Catalog;
using Jellyfin.Plugin.Terebi.Channels;
using Jellyfin.Plugin.Terebi.Epg;
using Jellyfin.Plugin.Terebi.Health;
using Jellyfin.Plugin.Terebi.LiveTv;
using Jellyfin.Plugin.Terebi.Sources;
using Jellyfin.Plugin.Terebi.Streaming;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Terebi;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<Storage.TerebiPaths>();
        serviceCollection.AddSingleton<LineupStore>();
        serviceCollection.AddSingleton<CatalogStore>();
        serviceCollection.AddSingleton<XtreamClient>();
        serviceCollection.AddSingleton<Logos.LogoIndex>();
        serviceCollection.AddSingleton<Logos.LogoCache>();
        serviceCollection.AddSingleton<Logos.CategoryImages>();
        serviceCollection.AddSingleton<LiveTv.GuideRefreshQueue>();
        serviceCollection.AddSingleton<LineupBuilder>();
        serviceCollection.AddSingleton<SourceRefresher>();
        serviceCollection.AddSingleton<HealthChecker>();
        serviceCollection.AddSingleton<EpgService>();
        serviceCollection.AddSingleton<StreamManager>();
        serviceCollection.AddSingleton<ChannelNameSync>();
        serviceCollection.AddSingleton<ILiveTvService, TerebiLiveTvService>();
        serviceCollection.AddSingleton<IChannel, TerebiChannel>();
        serviceCollection.AddHostedService<Web.WebTransformHost>();
        serviceCollection.AddHostedService<PlayedStateGuard>();
        serviceCollection.AddHostedService<NowPlayingSync>();
        serviceCollection.AddHostedService<LiveTv.GuideQueueWarmup>();
        serviceCollection.AddSingleton<StreamWatchdog>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<StreamWatchdog>());
    }
}
