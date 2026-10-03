using System.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Airtime.Channel;

/// <summary>
/// One Jellyfin channel folder whose items are the lineups. Playing one joins the clock.
/// </summary>
public sealed class AirtimeChannel : IChannel, IRequiresMediaInfoCallback
{
    internal static IServerApplicationHost? AppHost { get; set; }

    public AirtimeChannel()
    {
    }

    public string Name => "Airtime";

    public string Description => "Constant channels from your library.";

    public string DataVersion => "3";

    public string HomePageUrl => string.Empty;

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = [ChannelMediaContentType.Episode, ChannelMediaContentType.Movie],
            MediaTypes = [ChannelMediaType.Video],
            MaxPageSize = 100,
            AutoRefreshLevels = 0,
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
        try
        {
            if (!string.IsNullOrEmpty(query.FolderId))
            {
                return Task.FromResult(new ChannelItemResult { Items = [], TotalRecordCount = 0 });
            }

            var channels = Plugin.Instance?.Configuration.Channels ?? [];
            var items = new List<ChannelItemInfo>();
            foreach (var channel in channels.OrderBy(channel => channel.Number))
            {
                if (string.IsNullOrWhiteSpace(channel.Id) || string.IsNullOrWhiteSpace(channel.Name))
                {
                    continue;
                }

                items.Add(new ChannelItemInfo
                {
                    Id = TuneId(channel.Id),
                    Name = $"{channel.Number:00}  {channel.Name}",
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Episode,
                    Overview = string.IsNullOrWhiteSpace(channel.Tagline) ? "On the clock." : channel.Tagline,
                    IsLiveStream = true,
                    IndexNumber = channel.Number,
                });
            }

            return Task.FromResult(new ChannelItemResult
            {
                Items = items,
                TotalRecordCount = items.Count,
            });
        }
        catch (Exception)
        {
            return Task.FromResult(new ChannelItemResult { Items = [], TotalRecordCount = 0 });
        }
    }

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        try
        {
            var channelId = id.StartsWith("airtime-tune-", StringComparison.Ordinal) ? id["airtime-tune-".Length..] : id;
            var channel = Plugin.Instance?.Configuration.Channels.FirstOrDefault(item => string.Equals(item.Id, channelId, StringComparison.OrdinalIgnoreCase));
            if (channel is null)
            {
                return Task.FromResult<IEnumerable<MediaSourceInfo>>([]);
            }

            var baseUrl = string.Empty;
            try
            {
                baseUrl = AppHost?.GetSmartApiUrl(IPAddress.Loopback).TrimEnd('/') ?? string.Empty;
            }
            catch (Exception)
            {
                baseUrl = string.Empty;
            }

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
                    SupportsTranscoding = false,
                    SupportsProbing = false,
                    Name = channel.Name,
                    MediaStreams =
                    [
                        new MediaStream
                        {
                            Type = MediaStreamType.Video,
                            Index = 0,
                            Codec = "h264",
                            Width = 1280,
                            Height = 720,
                            IsInterlaced = false,
                        },
                        new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            Index = 1,
                            Codec = "aac",
                            Channels = 2,
                            SampleRate = 48000,
                        },
                    ],
                },
            ];
            return Task.FromResult(sources);
        }
        catch (Exception)
        {
            return Task.FromResult<IEnumerable<MediaSourceInfo>>([]);
        }
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
            try
            {
                plugin.SaveConfiguration();
            }
            catch (Exception)
            {
                // Playback can use the key for this run even if the file is not written yet.
            }
        }

        return config.StreamKey;
    }

    public static double SecondsNow()
    {
        var now = DateTime.Now;
        return (now.Hour * 3600) + (now.Minute * 60) + now.Second + (now.Millisecond / 1000d);
    }
}
