using Jellyfin.Plugin.Airtime.Api;
using Jellyfin.Plugin.Airtime.Live;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Airtime.Channel;

/// <summary>
/// Wires the channel, the Live TV tuner, and the tune endpoint.
/// None of them is an MVC controller, so a playback failure cannot stop the server from starting.
/// </summary>
public sealed class AirtimeServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        AirtimeChannel.AppHost = applicationHost;
        serviceCollection.AddSingleton<IChannel, AirtimeChannel>();
        serviceCollection.AddSingleton<ITunerHost, AirtimeTunerHost>();
        serviceCollection.AddSingleton<IListingsProvider, AirtimeListingsProvider>();
        serviceCollection.AddSingleton<IHostedService, AirtimeLiveTvInstaller>();
        serviceCollection.AddSingleton<IStartupFilter, AirtimeStartupFilter>();
    }
}