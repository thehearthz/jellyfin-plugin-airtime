using System.Net;
using Jellyfin.Plugin.Airtime.Scheduling;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
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

    internal static ILibraryManager? Library { get; set; }

    public AirtimeChannel()
    {
    }

    public string Name => "Airtime";

    public string Description => "Constant channels from your library.";

    public string DataVersion
    {
        get
        {
            try
            {
                var library = Library;
                var channels = Plugin.Instance?.Configuration.Channels;
                if (library is null || channels is null)
                {
                    return "5";
                }

                var stamp = string.Join(',', channels.Select(channel =>
                {
                    var slots = LibraryCatalog.Lineup(library, channel);
                    return ((int)ScheduleBuilder.At(slots, SecondsNow()).Slot.Start).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }));
                return "5-" + stamp;
            }
            catch
            {
                return "5";
            }
        }
    }

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

                var label = $"{channel.Number:00}  {channel.Name}";
                var overview = string.IsNullOrWhiteSpace(channel.Tagline) ? "On the clock." : channel.Tagline;
                string? image = null;
                var stamp = 0;
                try
                {
                    if (Library is not null)
                    {
                        var slots = LibraryCatalog.Lineup(Library, channel);
                        var on = AirtimeGuide.OnNow(slots, SecondsNow());
                        stamp = (int)on.Now.Start;
                        if (!string.IsNullOrWhiteSpace(on.Now.Title) && !string.Equals(on.Now.Title, "Station hold", StringComparison.Ordinal))
                        {
                            label += " · " + on.Now.Title;
                        }

                        if (!string.IsNullOrWhiteSpace(on.Next))
                        {
                            overview = "Next: " + on.Next;
                        }

                        if (!string.IsNullOrWhiteSpace(on.Now.Image) && File.Exists(on.Now.Image))
                        {
                            image = on.Now.Image;
                        }
                    }
                }
                catch
                {
                    // The name without a program is still a channel.
                }

                items.Add(new ChannelItemInfo
                {
                    Id = TuneId(channel.Id) + ":" + stamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Name = label,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Episode,
                    Overview = overview,
                    ImageUrl = image,
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
            var colon = channelId.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                channelId = channelId[..colon];
            }
            var channel = Plugin.Instance?.Configuration.Channels.FirstOrDefault(item => string.Equals(item.Id, channelId, StringComparison.OrdinalIgnoreCase));
            if (channel is null)
            {
                return Task.FromResult<IEnumerable<MediaSourceInfo>>([]);
            }

            var transcode = Plugin.Instance?.Configuration.Transcode ?? false;
            var baseUrl = LocalApiRoot();

            var key = StreamKey();
            List<MediaStream> streams = transcode
                ?
                [
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = 0,
                        Codec = "h264",
                        Width = 854,
                        Height = 480,
                        IsInterlaced = false,
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = 1,
                        Codec = "aac",
                        Channels = 2,
                        SampleRate = 44100,
                    },
                ]
                :
                [
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = -1,
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = -1,
                    },
                ];
            IEnumerable<MediaSourceInfo> sources =
            [
                new MediaSourceInfo
                {
                    Id = channel.Id,
                    Path = $"{baseUrl}/Airtime/Tune/{channel.Id}?key={key}",
                    Protocol = MediaProtocol.Http,
                    Container = "ts",
                    IsInfiniteStream = true,
                    IsRemote = false,
                    SupportsDirectPlay = false,
                    SupportsDirectStream = false,
                    SupportsTranscoding = true,
                    SupportsProbing = !transcode,
                    RequiresOpening = false,
                    ReadAtNativeFramerate = true,
                    Name = channel.Name,
                    MediaStreams = streams,
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

    private static string LocalApiRoot()
    {
        var host = AppHost;
        if (host is null)
        {
            return string.Empty;
        }

        try
        {
            // Always the server's own HTTP port. A published or LAN address is for browsers, and ffmpeg cannot use it.
            return host.GetLocalApiUrl("127.0.0.1", Uri.UriSchemeHttp, host.HttpPort).TrimEnd('/');
        }
        catch (Exception)
        {
            try
            {
                return host.GetApiUrlForLocalAccess(IPAddress.Loopback, false).TrimEnd('/');
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

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
