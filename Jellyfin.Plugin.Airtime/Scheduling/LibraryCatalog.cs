using System.Collections.Concurrent;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Airtime.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Airtime.Scheduling;

/// <summary>
/// Reads shows, movies, and tagged commercial spots out of the Jellyfin library.
/// Queries stay in memory. The database is not walked again until the channel changes.
/// </summary>
public sealed class LibraryCatalog
{
    private static readonly ConcurrentDictionary<string, (DateTime Built, string Signature, IReadOnlyList<Slot> Slots)> Cache = new();
    private static (DateTime Built, IReadOnlyList<string> Names)? GenreCache;

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

    public static void Clear()
    {
        Cache.Clear();
        GenreCache = null;
    }

    public IReadOnlyList<string> Genres()
    {
        if (GenreCache is { } cached && DateTime.UtcNow - cached.Built < TimeSpan.FromHours(12))
        {
            return cached.Names;
        }

        try
        {
            var result = _library.GetGenres(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Series],
                EnableTotalRecordCount = false,
            });
            var names = result.Items
                .Select(row => row.Item1.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            GenreCache = (DateTime.UtcNow, names);
            return names;
        }
        catch
        {
            return GenreCache?.Names ?? [];
        }
    }

    public IReadOnlyList<LibraryHit> Search(string? term, bool spots)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = spots
                ? [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video]
                : [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            EnableTotalRecordCount = false,
            Limit = 20,
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

    public IReadOnlyList<LibraryHit> Lookup(IEnumerable<string> ids)
    {
        var hits = new List<LibraryHit>();
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

            var hit = ToHit(item);
            if (hit is not null)
            {
                hits.Add(hit);
            }
        }

        return hits;
    }

    public IReadOnlyList<LibraryHit> TaggedSpots()
    {
        var hits = new List<LibraryHit>();
        foreach (var tag in SpotTags.Values)
        {
            var items = _library.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
                Tags = [tag],
                IsVirtualItem = false,
                EnableTotalRecordCount = false,
                Limit = 40,
                OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
            });
            hits.AddRange(items.Select(ToHit).Where(hit => hit is not null).Cast<LibraryHit>());
        }

        return hits
            .GroupBy(hit => hit.Id)
            .Select(group => group.First())
            .ToList();
    }

    public object Suggest()
    {
        var known = Genres().ToList();
        var knownSet = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        var plan = new (string Label, int Start, int End, bool Movies, string[] Genres)[]
        {
            ("Night movies", 0, 360, true, ["Horror", "Thriller", "Action", "Crime", "Mystery"]),
            ("Morning", 360, 540, false, ["Animation", "Kids", "Children", "Family"]),
            ("Daytime", 540, 1020, false, ["Comedy", "Sitcom", "Family"]),
            ("Afternoon", 1020, 1140, false, ["Animation", "Comedy", "Adventure"]),
            ("Primetime", 1140, 1380, false, ["Drama", "Action", "Crime", "Science Fiction"]),
            ("Late movie", 1380, 1440, true, ["Action", "Drama", "Thriller", "Comedy"]),
        };

        var blocks = new List<object>();
        foreach (var part in plan)
        {
            var genre = part.Genres.FirstOrDefault(knownSet.Contains) ?? known.FirstOrDefault() ?? string.Empty;
            var items = Pick(genre, part.Movies, 6);
            if (items.Count == 0)
            {
                foreach (var fallback in known)
                {
                    items = Pick(fallback, part.Movies, 6);
                    if (items.Count > 0)
                    {
                        genre = fallback;
                        break;
                    }
                }
            }

            blocks.Add(new
            {
                label = part.Label,
                startMinute = part.Start,
                endMinute = part.End,
                genres = genre,
                itemIds = string.Join(',', items.Select(item => item.Id)),
                items = items.Select(item => new
                {
                    id = item.Id,
                    name = item.Name,
                    kind = item.IsSeries ? "Series" : "Movie",
                    year = item.Year,
                }),
            });
        }

        return new
        {
            name = "Library mix",
            tagline = "A day built from your library.",
            mode = "auto",
            blocks,
        };
    }

    private List<LibraryHit> Pick(string genre, bool movies, int limit)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return [];
        }

        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = movies ? [BaseItemKind.Movie] : [BaseItemKind.Series],
            Genres = [genre],
            IsVirtualItem = false,
            EnableTotalRecordCount = false,
            Limit = limit,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
        };
        var hits = _library.GetItemList(query)
            .Select(ToHit)
            .Where(hit => hit is not null)
            .Cast<LibraryHit>()
            .ToList();
        if (hits.Count > 0 || movies)
        {
            return hits;
        }

        query.IncludeItemTypes = [BaseItemKind.Movie];
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
                EnableTotalRecordCount = false,
                Limit = 40,
                OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
            });
            clips.AddRange(items.Select(ToClip).Where(clip => clip is not null).Cast<Clip>());
        }

        return clips
            .GroupBy(clip => clip.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    public IReadOnlyList<Clip> SpotsFor(ChannelOptions channel)
    {
        var chosen = new List<Clip>();
        foreach (var spot in channel.Spots ?? [])
        {
            if (Guid.TryParse(spot.Id, out var id))
            {
                var item = _library.GetItemById(id);
                var clip = item is null ? null : ToClip(item, 8);
                if (clip is not null)
                {
                    chosen.Add(clip);
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(spot.Url))
            {
                continue;
            }

            var seconds = spot.DurationSeconds >= 8 ? spot.DurationSeconds : 30;
            chosen.Add(new Clip
            {
                Path = spot.Url.Trim(),
                DurationSeconds = seconds,
                Title = string.IsNullOrWhiteSpace(spot.Name) ? "Commercial" : spot.Name.Trim(),
                Detail = "Online",
            });
        }

        if (chosen.Count > 0)
        {
            return chosen;
        }

        return Spots(Split(channel.SpotTypes));
    }

    public IReadOnlyList<Clip> ProgramsFor(BlockOptions block)
    {
        return ProgramsFor(block, false, DateOnly.FromDateTime(DateTime.Now));
    }

    public IReadOnlyList<Clip> ProgramsFor(BlockOptions block, bool byDay, DateOnly day)
    {
        if (!byDay)
        {
            return ProgramsForFixed(block);
        }

        var titles = TitlesFor(block);
        if (titles.Count == 0)
        {
            return ProgramsForFixed(block);
        }

        // Four titles per date, then the next four the next day, so the same set does not air two days running.
        var window = Window(titles, day.DayNumber, 4);
        return Expand(window, 40);
    }

    private IReadOnlyList<Clip> ProgramsForFixed(BlockOptions block)
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
                        EnableTotalRecordCount = false,
                        Limit = 400,
                        OrderBy = [(ItemSortBy.AiredEpisodeOrder, SortOrder.Ascending)],
                    });
                    chosen.AddRange(episodes
                        .OfType<Episode>()
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

        return Expand(TitlesFromGenres(Split(block.Genres), 160, episodes: true), 1);
    }

    private List<BaseItem> TitlesFor(BlockOptions block)
    {
        var genres = Split(block.Genres);
        if (genres.Count > 0)
        {
            var fromGenres = TitlesFromGenres(genres, 24, episodes: false);
            if (fromGenres.Count > 0)
            {
                return fromGenres;
            }
        }

        var titles = new List<BaseItem>();
        foreach (var raw in Split(block.ItemIds))
        {
            if (!Guid.TryParse(raw, out var id))
            {
                continue;
            }

            var item = _library.GetItemById(id);
            if (item is not null)
            {
                titles.Add(item);
            }
        }

        return titles
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
    }

    private List<BaseItem> TitlesFromGenres(List<string> genres, int limit, bool episodes)
    {
        var found = new List<BaseItem>();
        foreach (var genre in genres)
        {
            found.AddRange(_library.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = episodes
                    ? [BaseItemKind.Movie, BaseItemKind.Episode]
                    : [BaseItemKind.Movie, BaseItemKind.Series],
                Genres = [genre],
                IsVirtualItem = false,
                EnableTotalRecordCount = false,
                Limit = limit,
                OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
            }));
        }

        return found
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .OrderBy(item => item.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<Clip> Expand(IReadOnlyList<BaseItem> items, int episodeLimit)
    {
        var chosen = new List<Clip>();
        foreach (var item in items)
        {
            if (item is Series || item.IsFolder)
            {
                var episodes = _library.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    AncestorIds = [item.Id],
                    IncludeItemTypes = [BaseItemKind.Episode],
                    IsVirtualItem = false,
                    EnableTotalRecordCount = false,
                    Limit = episodeLimit,
                    OrderBy = [(ItemSortBy.AiredEpisodeOrder, SortOrder.Ascending)],
                });
                chosen.AddRange(episodes
                    .OfType<Episode>()
                    .Select(episode => ToClip(episode))
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

    private static List<T> Window<T>(IReadOnlyList<T> items, int dayNumber, int size)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var count = Math.Min(size, items.Count);
        var span = items.Count <= size ? 1 : size;
        var start = (int)(((dayNumber * (long)span) % items.Count + items.Count) % items.Count);
        var window = new List<T>(count);
        for (var i = 0; i < count; i++)
        {
            window.Add(items[(start + i) % items.Count]);
        }

        return window;
    }

    public static IReadOnlyList<Slot> Lineup(ILibraryManager library, ChannelOptions channel, DateOnly? day = null)
    {
        var today = day ?? DateOnly.FromDateTime(DateTime.Now);
        var signature = Signature(channel, today);
        var cacheKey = channel.Id + ":" + today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (Cache.TryGetValue(cacheKey, out var hit)
            && string.Equals(hit.Signature, signature, StringComparison.Ordinal)
            && DateTime.UtcNow - hit.Built < TimeSpan.FromHours(12))
        {
            return hit.Slots;
        }

        var auto = string.Equals(channel.Mode, "auto", StringComparison.OrdinalIgnoreCase);
        var catalog = new LibraryCatalog(library);
        var spots = catalog.SpotsFor(channel);
        var blocks = channel.Blocks.Select(block => (
            Label: string.IsNullOrWhiteSpace(block.Label) ? "Block" : block.Label,
            block.StartMinute,
            block.EndMinute,
            Programs: (IReadOnlyList<Clip>)catalog.ProgramsFor(block, auto, today))).ToList();
        var seed = channel.Id + ":" + channel.Number + ":" + channel.Name;
        if (auto)
        {
            seed += ":" + today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        var slots = ScheduleBuilder.Build(
            seed,
            blocks,
            spots,
            channel.CommercialEveryMinutes,
            channel.BreakSeconds,
            keepOrder: !auto,
            continueDay: auto ? -1 : today.DayNumber);
        Cache[cacheKey] = (DateTime.UtcNow, signature, slots);
        return slots;
    }

    private static string Signature(ChannelOptions channel, DateOnly day)
    {
        var builder = new StringBuilder();
        builder.Append(channel.Number).Append('\n');
        builder.Append(channel.Name).Append('\n');
        builder.Append(channel.CommercialEveryMinutes).Append('\n');
        builder.Append(channel.BreakSeconds).Append('\n');
        builder.Append(channel.SpotTypes).Append('\n');
        foreach (var spot in channel.Spots ?? [])
        {
            builder.Append(spot.Id).Append('|').Append(spot.Url).Append('|').Append(spot.DurationSeconds).Append('\n');
        }

        builder.Append(channel.Mode).Append('\n');
        builder.Append(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');

        foreach (var block in channel.Blocks ?? [])
        {
            builder.Append(block.StartMinute).Append('-').Append(block.EndMinute).Append('|');
            builder.Append(block.Genres).Append('|').Append(block.ItemIds).Append('\n');
        }

        return builder.ToString();
    }

    private static LibraryHit? ToHit(BaseItem item)
    {
        return new LibraryHit(item.Id, item.Name, item.GetType().Name, item.ProductionYear, item.IsFolder || item is Series);
    }

    private static Clip? ToClip(BaseItem item, int minSeconds = 20)
    {
        if (item.IsFolder || string.IsNullOrWhiteSpace(item.Path) || item.IsVirtualItem)
        {
            return null;
        }

        var runtime = item.RunTimeTicks.GetValueOrDefault();
        if (runtime < TimeSpan.FromSeconds(minSeconds).Ticks)
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
            Image = Still(item),
        };
    }

    private static string Still(BaseItem item)
    {
        try
        {
            var path = item.PrimaryImagePath;
            if (string.IsNullOrWhiteSpace(path) && item is Episode episode)
            {
                path = episode.Series?.PrimaryImagePath;
            }

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }
        catch
        {
            // A missing picture is not a reason to drop the program.
        }

        return string.Empty;
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
