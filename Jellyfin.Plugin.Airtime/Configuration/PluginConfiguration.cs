using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Airtime.Configuration;

/// <summary>
/// Saved channel desk. Edited from the Airtime page in the Jellyfin dashboard.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public bool Transcode { get; set; } = true;

    /// <summary>Checked on the tune URL so playback works without a browser session.</summary>
    public string StreamKey { get; set; } = string.Empty;

    public List<ChannelOptions> Channels { get; set; } = new();
}

/// <summary>
/// One constant channel.
/// </summary>
public class ChannelOptions
{
    public string Id { get; set; } = string.Empty;

    public int Number { get; set; } = 2;

    public string Name { get; set; } = string.Empty;

    public string Tagline { get; set; } = string.Empty;

    /// <summary>manual or auto. The schedule always comes from Blocks.</summary>
    public string Mode { get; set; } = "manual";

    public int CommercialEveryMinutes { get; set; } = 12;

    public int BreakSeconds { get; set; } = 60;

    /// <summary>Comma-separated: network, local, psa, promo.</summary>
    public string SpotTypes { get; set; } = "network,local";

    public List<BlockOptions> Blocks { get; set; } = new();
}

/// <summary>
/// A slice of the day. Genres and item ids are comma-separated so the dashboard can edit them as text.
/// </summary>
public class BlockOptions
{
    public string Label { get; set; } = string.Empty;

    public int StartMinute { get; set; }

    public int EndMinute { get; set; } = 1440;

    public string Genres { get; set; } = string.Empty;

    public string ItemIds { get; set; } = string.Empty;
}
