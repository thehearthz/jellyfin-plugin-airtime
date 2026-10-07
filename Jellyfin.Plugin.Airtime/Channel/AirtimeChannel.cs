using Jellyfin.Plugin.Airtime.Api;
using Jellyfin.Plugin.Airtime.Scheduling;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;

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

            var library = Library ?? AppHost?.ServiceProvider?.GetService<ILibraryManager>();
            if (library is null)
            {
                return Task.FromResult<IEnumerable<MediaSourceInfo>>([]);
            }

            Library = library;
            var plan = ScheduleBuilder.Playback(LibraryCatalog.Lineup(library, channel), SecondsNow(), 4 * 60 * 60);
            if (plan.Count == 0)
            {
                return Task.FromResult<IEnumerable<MediaSourceInfo>>([]);
            }

            // Jellyfin already feeds *.concat as `ffmpeg -f concat -safe 0` when VideoType is Dvd.
            // That is the only way absolute library paths play without a second HTTP hop.
            var path = AirtimeHttp.WriteConcatFile(channel.Id, plan);
            IEnumerable<MediaSourceInfo> sources =
            [
                new MediaSourceInfo
                {
                    Id = channel.Id,
                    Path = path,
                    Protocol = MediaProtocol.File,
                    Container = "mpegts",
                    VideoType = VideoType.Dvd,
                    IsInfiniteStream = true,
                    IsRemote = false,
                    SupportsDirectPlay = false,
                    SupportsDirectStream = false,
                    SupportsTranscoding = true,
                    SupportsProbing = false,
                    RequiresOpening = false,
                    ReadAtNativeFramerate = false,
                    IgnoreDts = true,
                    AnalyzeDurationMs = 5000,
                    UseMostCompatibleTranscodingProfile = true,
                    Name = channel.Name,
                    MediaStreams =
                    [
                        new MediaStream
                        {
                            Type = MediaStreamType.Video,
                            Index = 0,
                            Width = 1280,
                            Height = 720,
                            IsInterlaced = false,
                        },
                        new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            Index = 1,
                            Channels = 2,
                            SampleRate = 44100,
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
