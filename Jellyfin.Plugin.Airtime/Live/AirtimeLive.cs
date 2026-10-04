using System.Net.Http;
using Jellyfin.Plugin.Airtime.Channel;
using Jellyfin.Plugin.Airtime.Configuration;
using Jellyfin.Plugin.Airtime.Scheduling;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Airtime.Live;

/// <summary>
/// Adds Airtime to Live TV and fills the guide from the same clock the channel plays.
/// </summary>
public sealed class AirtimeTunerHost : ITunerHost, IConfigurableTunerHost
{
    public const string TunerId = "a7c3e1b46d204f5a9c182b8e4d0f6a32";

    private readonly IConfigurationManager _config;

    public AirtimeTunerHost(IConfigurationManager config)
    {
        _config = config;
    }

    public string Name => "Airtime";

    public string Type => "airtime";

    public bool IsSupported => true;

    public Task Validate(TunerHostInfo info)
    {
        return Task.CompletedTask;
    }

    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
    {
        return Task.FromResult(new List<TunerHostInfo> { Host() });
    }

    public Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
    {
        var hostId = HostId();
        var list = new List<ChannelInfo>();
        foreach (var channel in Channels())
        {
            var still = Still(channel);
            list.Add(new ChannelInfo
            {
                Id = AirtimeGuide.ChannelKey(channel.Id),
                TunerChannelId = channel.Id,
                TunerHostId = hostId,
                Number = channel.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = $"{channel.Number:00} {channel.Name}",
                ChannelType = ChannelType.TV,
                ImagePath = still,
                HasImage = !string.IsNullOrEmpty(still),
            });
        }

        return Task.FromResult(list);
    }

    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var channel = Find(channelId);
        if (channel is null)
        {
            return [];
        }

        var sources = await new AirtimeChannel().GetChannelItemMediaInfo(AirtimeChannel.TuneId(channel.Id), cancellationToken).ConfigureAwait(false);
        return sources.ToList();
    }

    public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        if (Find(channelId) is null)
        {
            throw new FileNotFoundException();
        }

        var sources = await GetChannelStreamMediaSources(channelId, cancellationToken).ConfigureAwait(false);
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(sources[0].Path))
        {
            throw new FileNotFoundException();
        }

        // Playback uses the media-source path, the same localhost URL as the channel folder.
        // Opening it here would call back into this server on the request thread.
        return new AirtimeLiveStream(sources[0], HostId(), streamId);
    }

    private string HostId()
    {
        try
        {
            var saved = _config.GetConfiguration<LiveTvOptions>("livetv").TunerHosts
                .FirstOrDefault(host => string.Equals(host.Type, Type, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(saved?.Id))
            {
                return saved.Id;
            }
        }
        catch
        {
            // The guide can still be built before Live TV config is written.
        }

        return TunerId;
    }

    private static string? Still(ChannelOptions channel)
    {
        try
        {
            var library = AirtimeChannel.Library;
            if (library is null)
            {
                return null;
            }

            var slots = LibraryCatalog.Lineup(library, channel);
            var on = AirtimeGuide.OnNow(slots, AirtimeChannel.SecondsNow()).Now.Image;
            return string.IsNullOrWhiteSpace(on) ? null : on;
        }
        catch
        {
            return null;
        }
    }

    internal static ChannelOptions? Find(string channelId)
    {
        var key = channelId.StartsWith("airtime-", StringComparison.OrdinalIgnoreCase)
            ? channelId["airtime-".Length..]
            : channelId;
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            key = key[..colon];
        }

        return Channels().FirstOrDefault(channel => string.Equals(channel.Id, key, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<ChannelOptions> Channels()
    {
        return (Plugin.Instance?.Configuration.Channels ?? [])
            .Where(channel => !string.IsNullOrWhiteSpace(channel.Id) && !string.IsNullOrWhiteSpace(channel.Name))
            .OrderBy(channel => channel.Number);
    }

    private static TunerHostInfo Host()
    {
        return new TunerHostInfo
        {
            Id = TunerId,
            Type = "airtime",
            FriendlyName = "Airtime",
            Url = "airtime://channels",
            AllowStreamSharing = true,
            TunerCount = 0,
        };
    }
}

/// <summary>
/// Guide data for the Airtime tuner. Channel ids match the tuner, so no manual mapping is required.
/// </summary>
public sealed class AirtimeListingsProvider : IListingsProvider
{
    public const string ProviderId = "a7c3e1b46d204f5a9c182b8e4d0f6a33";

    public string Name => "Airtime";

    public string Type => "airtime";

    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(ListingsProviderInfo info, string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        var channel = AirtimeTunerHost.Find(channelId);
        var library = AirtimeChannel.Library;
        if (channel is null || library is null)
        {
            return Task.FromResult<IEnumerable<ProgramInfo>>([]);
        }

        var programs = new List<ProgramInfo>();
        var first = DateOnly.FromDateTime(startDateUtc.ToLocalTime());
        var last = DateOnly.FromDateTime(endDateUtc.ToLocalTime());
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            IReadOnlyList<Slot> slots;
            try
            {
                slots = LibraryCatalog.Lineup(library, channel, day);
            }
            catch
            {
                continue;
            }

            programs.AddRange(AirtimeGuide.Programs(AirtimeGuide.ChannelKey(channel.Id), slots, day, startDateUtc, endDateUtc));
        }

        return Task.FromResult<IEnumerable<ProgramInfo>>(programs);
    }

    public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
    {
        return Task.CompletedTask;
    }

    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
    {
        return Task.FromResult(new List<NameIdPair> { new() { Name = "Airtime", Id = "airtime" } });
    }

    public Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
    {
        var list = (Plugin.Instance?.Configuration.Channels ?? [])
            .Where(channel => !string.IsNullOrWhiteSpace(channel.Id))
            .Select(channel => new ChannelInfo
            {
                Id = AirtimeGuide.ChannelKey(channel.Id),
                Number = channel.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = $"{channel.Number:00} {channel.Name}",
            })
            .ToList();
        return Task.FromResult(list);
    }
}

/// <summary>
/// Writes the Airtime tuner and guide into Live TV once the server is up.
/// </summary>
public sealed class AirtimeLiveTvInstaller : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServiceProvider _services;

    public AirtimeLiveTvInstaller(IHostApplicationLifetime lifetime, IServiceProvider services)
    {
        _lifetime = lifetime;
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(Install);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void Install()
    {
        try
        {
            AirtimeChannel.Library = _services.GetService<MediaBrowser.Controller.Library.ILibraryManager>();
            var configManager = _services.GetRequiredService<IConfigurationManager>();
            var options = configManager.GetConfiguration<LiveTvOptions>("livetv");
            var changed = false;
            var hosts = options.TunerHosts ?? [];
            if (!hosts.Any(host => string.Equals(host.Type, "airtime", StringComparison.OrdinalIgnoreCase)))
            {
                options.TunerHosts =
                [
                    .. hosts,
                    new TunerHostInfo
                    {
                        Id = AirtimeTunerHost.TunerId,
                        Type = "airtime",
                        FriendlyName = "Airtime",
                        Url = "airtime://channels",
                        AllowStreamSharing = true,
                        TunerCount = 0,
                    },
                ];
                changed = true;
            }

            var providers = options.ListingProviders ?? [];
            if (!providers.Any(provider => string.Equals(provider.Type, "airtime", StringComparison.OrdinalIgnoreCase)))
            {
                options.ListingProviders =
                [
                    .. providers,
                    new ListingsProviderInfo
                    {
                        Id = AirtimeListingsProvider.ProviderId,
                        Type = "airtime",
                        EnableAllTuners = true,
                        Path = "airtime",
                    },
                ];
                changed = true;
            }

            if (changed)
            {
                configManager.SaveConfiguration("livetv", options);
                QueueGuide();
            }

            if (Plugin.Instance is not null)
            {
                Plugin.Instance.ConfigurationChanged += (_, _) => QueueGuide();
            }
        }
        catch
        {
            // Live TV can be added later. A failure here must not stop the server.
        }
    }

    private void QueueGuide()
    {
        try
        {
            var tasks = _services.GetService<ITaskManager>();
            var guide = tasks?.ScheduledTasks.FirstOrDefault(task => task.ScheduledTask.Key == "RefreshGuide");
            if (guide is not null)
            {
                tasks!.QueueScheduledTask(guide.ScheduledTask, new TaskOptions());
            }
        }
        catch
        {
            // The daily guide task still runs if this queue is missed.
        }
    }
}

internal sealed class AirtimeLiveStream : ILiveStream
{
    private readonly HttpClient _client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private Stream? _body;

    public AirtimeLiveStream(MediaSourceInfo source, string tunerHostId, string streamId)
    {
        MediaSource = source;
        TunerHostId = tunerHostId;
        OriginalStreamId = streamId;
        UniqueId = Guid.NewGuid().ToString("n");
    }

    public int ConsumerCount { get; set; }

    public string OriginalStreamId { get; set; }

    public string TunerHostId { get; }

    public bool EnableStreamSharing => false;

    public MediaSourceInfo MediaSource { get; set; }

    public string UniqueId { get; }

    public async Task Open(CancellationToken openCancellationToken)
    {
        _body = await _client.GetStreamAsync(MediaSource.Path, openCancellationToken).ConfigureAwait(false);
    }

    public Task Close()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public Stream GetStream()
    {
        return _body ?? Stream.Null;
    }

    public void Dispose()
    {
        _body?.Dispose();
        _client.Dispose();
    }
}
