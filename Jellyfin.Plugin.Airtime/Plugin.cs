using System.Globalization;
using Jellyfin.Plugin.Airtime.Configuration;
using Jellyfin.Plugin.Airtime.Scheduling;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Airtime;

/// <summary>
/// Airtime turns library shows and movies into channels that stay on the clock.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginId = "a7c3e1b4-6d20-4f5a-9c18-2b8e4d0f6a31";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        ConfigurationChanged = (_, _) => LibraryCatalog.Clear();
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Airtime";

    public override string Description => "Constant channels from your library, with commercial breaks.";

    public override Guid Id => Guid.Parse(PluginId);

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            },
        ];
    }
}
