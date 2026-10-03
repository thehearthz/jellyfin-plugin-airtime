using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Airtime.Channel;

/// <summary>
/// Wires the Airtime channel into Jellyfin. The server does not discover channels on its own.
/// </summary>
public sealed class AirtimeServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IChannel, AirtimeChannel>();
    }
}
