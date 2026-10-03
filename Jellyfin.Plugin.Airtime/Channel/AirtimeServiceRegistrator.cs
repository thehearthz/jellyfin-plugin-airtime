using Jellyfin.Plugin.Airtime.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Airtime.Channel;

/// <summary>
/// Wires the channel and the tune endpoint. Neither one is an MVC controller, so a playback
/// failure cannot stop the server from starting.
/// </summary>
public sealed class AirtimeServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        AirtimeChannel.AppHost = applicationHost;
        serviceCollection.AddSingleton<IChannel, AirtimeChannel>();
        serviceCollection.AddSingleton<IStartupFilter, AirtimeStartupFilter>();
    }
}
