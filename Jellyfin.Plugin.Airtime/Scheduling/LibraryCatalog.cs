using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Airtime.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Airtime.Scheduling;

/// <summary>
/// Reads shows, movies, and tagged commercial spots out of the Jellyfin library.
/// </summary>
public sealed class LibraryCatalog
{
    private static readonly ConcurrentDictionary<string, (DateTime Built, IReadOnlyList<Slot> Slots)> Cache = new();

    public static readonly IReadOnlyDictionary<string, string> SpotTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["network"] = "airtime-network",
        ["local"] = "airtime-local",
        ["psa"] = "airtime-psa",
        ["promo"] = "airtime-promo",
    };

    private readonly ILibraryManager _library;

    public LibraryCatalog(ILibraryManager library)
    {
        _library = library;
    }

    public IReadOnlyList<string> Genres()
    {
        try
        {
            var result = _library.GetGenres(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Series],
            });
            return result.Items
                .Select(row => row.Item1.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<LibraryHit> Search(string? term)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            Limit = 40,
        };
        if (!string.IsNullOrWhiteSpace(term))
        {
            query.SearchTerm = term.Trim();
        }

        return _library.GetItemList(query)
            .Select(ToHit)
            .Where(hit => hit is not null)
            .Cast<LibraryHit>()
            .ToList();
    }

    public IReadOnlyList<Clip> Spots(IEnumerable<string> types)
    {
        var clips = new List<Clip>();
        foreach (var type in types)
        {
            if (!SpotTags.TryGetValue(type.Trim(), out var tag))
            {
                continue;
            }

            var items = _library.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
                Tags = [tag],
                IsVirtualItem = false,
            });
            clips.AddRange(items.Select(ToClip).Where(clip => clip is not null).Cast<Clip>());
        }

        return clips
            .GroupBy(clip => clip.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    public IReadOnlyList<Clip> ProgramsFor(BlockOptions block)
    {
        var ids = Split(block.ItemIds);
        if (ids.Count > 0)
        {
            var chosen = new List<Clip>();
            foreach (var raw in ids)
            {
                if (!Guid.TryParse(raw, out var id))
                {
                    continue;
                }

                var item = _library.GetItemById(id);
                if (item is null)
                {
                    continue;
                }

                if (item is Series || item.IsFolder)
                {
                    var episodes = _library.GetItemList(new InternalItemsQuery
                    {
                        Recursive = true,
                        AncestorIds = [item.Id],
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                    });
                    chosen.AddRange(episodes
                        .OfType<Episode>()
                        .OrderBy(episode => episode.ParentIndexNumber ?? 0)
                        .ThenBy(episode => episode.IndexNumber ?? 0)
                        .Select(ToClip)
                        .Where(clip => clip is not null)
                        .Cast<Clip>());
                }
                else
                {
                    var clip = ToClip(item);
                    if (clip is not null)
                    {
                        chosen.Add(clip);
                    }
                }
            }

            return chosen;
        }

        var genres = Split(block.Genres);
        if (genres.Count == 0)
        {
            return [];
        }

        var found = new List<BaseItem>();
        foreach (var genre in genres)
        {
            found.AddRange(_library.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Genres = [genre],
                IsVirtualItem = false,
            }));
        }

        return found
            .GroupBy(item => item.Id)
            .Select(group => ToClip(group.First()))
            .Where(clip => clip is not null)
            .Cast<Clip>()
            .ToList();
    }

    public static IReadOnlyList<Slot> Lineup(ILibraryManager library, ChannelOptions channel)
    {
        var key = string.Join('|', channel.Id, channel.Name, channel.Number, channel.CommercialEveryMinutes, channel.BreakSeconds, channel.SpotTypes, channel.Blocks.Count);
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.Built < TimeSpan.FromSeconds(45))
        {
            return hit.Slots;
        }

        var catalog = new LibraryCatalog(library);
        var spots = catalog.Spots(Split(channel.SpotTypes));
        var blocks = channel.Blocks.Select(block => (
            Label: string.IsNullOrWhiteSpace(block.Label) ? "Block" : block.Label,
            block.StartMinute,
            block.EndMinute,
            Programs: (IReadOnlyList<Clip>)catalog.ProgramsFor(block))).ToList();
        var slots = ScheduleBuilder.Build(
            channel.Id + ":" + channel.Number + ":" + channel.Name,
            blocks,
            spots,
            channel.CommercialEveryMinutes,
            channel.BreakSeconds);
        Cache[key] = (DateTime.UtcNow, slots);
        return slots;
    }

    private static LibraryHit? ToHit(BaseItem item)
    {
        return new LibraryHit(item.Id, item.Name, item.GetType().Name, item.ProductionYear, item.IsFolder || item is Series);
    }

    private static Clip? ToClip(BaseItem item)
    {
        if (item.IsFolder || string.IsNullOrWhiteSpace(item.Path) || item.IsVirtualItem)
        {
            return null;
        }

        var runtime = item.RunTimeTicks.GetValueOrDefault();
        if (runtime < TimeSpan.FromSeconds(20).Ticks)
        {
            return null;
        }

        string title;
        var detail = string.Empty;
        if (item is Episode episode)
        {
            title = string.IsNullOrWhiteSpace(episode.SeriesName) ? episode.Name : episode.SeriesName;
            detail = episode.Name ?? string.Empty;
        }
        else
        {
            title = item.Name ?? "Program";
        }

        return new Clip
        {
            Path = item.Path,
            DurationSeconds = runtime / (double)TimeSpan.TicksPerSecond,
            Title = title,
            Detail = detail,
        };
    }

    public static List<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .ToList();
    }
}

public sealed record LibraryHit(Guid Id, string Name, string Kind, int? Year, bool IsSeries);
