using System.Net;
using Jellyfin.Plugin.Airtime.Scheduling;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Airtime.Channel;

/// <summary>
/// One Jellyfin channel folder whose items are the lineups. Playing one joins the clock.
/// </summary>
public sealed class AirtimeChannel : IChannel, IRequiresMediaInfoCallback
{
    private readonly ILibraryManager _library;
    private readonly IServerApplicationHost _appHost;
    private readonly IHttpContextAccessor _http;

    public AirtimeChannel(ILibraryManager library, IServerApplicationHost appHost, IHttpContextAccessor http)
    {
        _library = library;
        _appHost = appHost;
        _http = http;
    }

    public string Name => "Airtime";

    public string Description => "Constant channels from your library.";

    public string DataVersion => DateTime.Now.ToString("yyyyMMddHHmm");

    public string HomePageUrl => string.Empty;

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = [ChannelMediaContentType.Episode, ChannelMediaContentType.Movie],
            MediaTypes = [ChannelMediaType.Video],
            MaxPageSize = 100,
            AutoRefreshLevels = 2,
        };
    }

    public bool IsEnabledFor(string userId) => true;

    public IEnumerable<ImageType> GetSupportedChannelImages() => [];

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DynamicImageResponse { HasImage = false });
    }

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var channels = Plugin.Instance?.Configuration.Channels ?? [];
        if (channels.Count == 0)
        {
            return Task.FromResult(new ChannelItemResult
            {
                Items =
                [
                    new ChannelItemInfo
                    {
                        Id = "airtime-empty",
                        Name = "No channels yet",
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Container,
                        Overview = "Open Dashboard, Plugins, Airtime, and add a channel.",
                    },
                ],
                TotalRecordCount = 1,
            });
        }

        var items = new List<ChannelItemInfo>();
        foreach (var channel in channels.OrderBy(channel => channel.Number))
        {
            var slots = LibraryCatalog.Lineup(_library, channel);
            var now = SecondsNow();
            var (slot, _, into) = slots.Count > 0
                ? ScheduleBuilder.At(slots, now)
                : (new Slot { Title = "Nothing scheduled" }, 0, 0d);
            var left = Math.Max(0, slot.Duration - into);
            items.Add(new ChannelItemInfo
            {
                Id = TuneId(channel.Id),
                Name = $"{channel.Number:00}  {channel.Name}",
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Episode,
                Overview = string.IsNullOrWhiteSpace(slot.Title)
                    ? channel.Tagline
                    : $"{slot.Block}: {slot.Title}{(string.IsNullOrWhiteSpace(slot.Detail) ? string.Empty : " — " + slot.Detail)}. {TimeLeft(left)} left in this piece.",
                IsLiveStream = true,
                DateModified = DateTime.UtcNow,
                IndexNumber = channel.Number,
            });
        }

        return Task.FromResult(new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count,
        });
    }

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        var channelId = id.StartsWith("airtime-tune-", StringComparison.Ordinal) ? id["airtime-tune-".Length..] : id;
        var channel = Plugin.Instance?.Configuration.Channels.FirstOrDefault(item => string.Equals(item.Id, channelId, StringComparison.OrdinalIgnoreCase));
        if (channel is null)
        {
            return Task.FromResult<IEnumerable<MediaSourceInfo>>([]);
        }

        var request = _http.HttpContext?.Request;
        var baseUrl = (request is null
            ? _appHost.GetApiUrlForLocalAccess(IPAddress.Loopback, false)
            : _appHost.GetSmartApiUrl(request)).TrimEnd('/');
        var key = StreamKey();

        IEnumerable<MediaSourceInfo> sources =
        [
            new MediaSourceInfo
            {
                Id = channel.Id,
                Path = $"{baseUrl}/Airtime/Tune/{channel.Id}?key={key}",
                Protocol = MediaProtocol.Http,
                Container = "ts",
                IsInfiniteStream = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                Name = channel.Name,
            },
        ];
        return Task.FromResult(sources);
    }

    public static string TuneId(string channelId) => "airtime-tune-" + channelId;

    public static string StreamKey()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return string.Empty;
        }

        var config = plugin.Configuration;
        if (string.IsNullOrWhiteSpace(config.StreamKey))
        {
            config.StreamKey = Guid.NewGuid().ToString("n");
            plugin.SaveConfiguration();
        }

        return config.StreamKey;
    }

    public static double SecondsNow()
    {
        var now = DateTime.Now;
        return (now.Hour * 3600) + (now.Minute * 60) + now.Second + (now.Millisecond / 1000d);
    }

    private static string TimeLeft(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : $"{span.Minutes}m {span.Seconds:00}s";
    }
}
