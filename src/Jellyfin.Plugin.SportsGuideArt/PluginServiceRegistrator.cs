using Jellyfin.Plugin.SportsGuideArt.Caching;
using Jellyfin.Plugin.SportsGuideArt.Imaging;
using Jellyfin.Plugin.SportsGuideArt.Logos;
using Jellyfin.Plugin.SportsGuideArt.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SportsGuideArt;

/// <summary>Registers the plugin's services.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ImageCache>();
        serviceCollection.AddSingleton<ArtIndex>();
        serviceCollection.AddSingleton<EspnTeamLookup>();
        serviceCollection.AddSingleton<TheSportsDbTeamLookup>();
        serviceCollection.AddSingleton<TeamResolver>();
        serviceCollection.AddSingleton<MatchupImageRenderer>();
        serviceCollection.AddSingleton<GuideArtFiller>();
        serviceCollection.AddHostedService<GuideRefreshListener>();
    }
}
