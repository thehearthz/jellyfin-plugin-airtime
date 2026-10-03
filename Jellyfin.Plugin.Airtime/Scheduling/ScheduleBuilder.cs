namespace Jellyfin.Plugin.Airtime.Scheduling;

/// <summary>
/// A file the channel can air.
/// </summary>
public sealed class Clip
{
    public required string Path { get; init; }

    public required double DurationSeconds { get; init; }

    public required string Title { get; init; }

    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// One slice of the broadcast day.
/// </summary>
public sealed class Slot
{
    public double Start { get; init; }

    public double Duration { get; init; }

    public string? Path { get; init; }

    public double FileOffset { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public bool IsSpot { get; init; }

    public string Block { get; init; } = string.Empty;
}

/// <summary>
/// Builds a 24-hour lineup. The same channel always produces the same day, so everyone joins the same moment.
/// </summary>
public static class ScheduleBuilder
{
    public const double Day = 24 * 60 * 60;

    public static IReadOnlyList<Slot> Build(
        string seed,
        IReadOnlyList<(string Label, int StartMin, int EndMin, IReadOnlyList<Clip> Programs)> blocks,
        IReadOnlyList<Clip> spots,
        int everyMinutes,
        int breakSeconds)
    {
        var rand = Rng(Hash(seed));
        var slots = new List<Slot>();
        var cursor = 0d;
        var every = Math.Max(3, everyMinutes) * 60d;
        var ordered = blocks.OrderBy(b => b.StartMin).ToList();

        void Slate(double until, string label)
        {
            if (until - cursor >= 1)
            {
                slots.Add(new Slot
                {
                    Start = cursor,
                    Duration = until - cursor,
                    Title = "Station hold",
                    Block = label,
                });
            }

            cursor = until;
        }

        foreach (var block in ordered)
        {
            var start = Math.Clamp(block.StartMin, 0, 1440) * 60d;
            var end = Math.Clamp(block.EndMin, 0, 1440) * 60d;
            if (start > cursor)
            {
                Slate(start, "Between blocks");
            }

            if (end <= cursor)
            {
                continue;
            }

            var pool = Shuffle(block.Programs.Where(p => p.DurationSeconds >= 20 && !string.IsNullOrWhiteSpace(p.Path)).ToList(), rand);
            if (pool.Count == 0)
            {
                Slate(end, block.Label);
                continue;
            }

            var sinceBreak = 0d;
            var order = 0;
            Clip? current = null;
            var remain = 0d;
            var played = 0d;

            while (cursor < end - 1)
            {
                var room = end - cursor;
                if (room < 20)
                {
                    Slate(end, block.Label);
                    break;
                }

                var untilBreak = spots.Count > 0 ? Math.Max(0, every - sinceBreak) : room;
                if (spots.Count > 0 && untilBreak < 15)
                {
                    var before = cursor;
                    var filled = 0d;
                    var target = Math.Min(breakSeconds, room - 15);
                    var guard = 0;
                    if (target >= 12)
                    {
                        while (filled < target - 3 && guard < 6 && cursor < end - 12)
                        {
                            guard++;
                            var spot = spots[(int)(rand() * spots.Count)];
                            var dur = Math.Min(spot.DurationSeconds, target - filled);
                            dur = Math.Min(dur, end - cursor);
                            if (dur < 8)
                            {
                                break;
                            }

                            slots.Add(new Slot
                            {
                                Start = cursor,
                                Duration = dur,
                                Path = spot.Path,
                                FileOffset = 0,
                                Title = spot.Title,
                                Detail = spot.Detail,
                                IsSpot = true,
                                Block = block.Label,
                            });
                            cursor += dur;
                            filled += dur;
                        }

                        if (cursor > before)
                        {
                            sinceBreak = 0;
                            continue;
                        }
                    }
                }

                if (current is null || remain <= 0)
                {
                    var sample = pool[0];
                    if (room < Math.Min(8 * 60, sample.DurationSeconds * 0.35))
                    {
                        Slate(end, block.Label);
                        break;
                    }

                    current = pool[order % pool.Count];
                    order++;
                    remain = current.DurationSeconds;
                    played = 0;
                }

                var sliceCap = spots.Count > 0 ? Math.Max(15, every - sinceBreak) : end - cursor;
                var slice = Math.Min(remain, Math.Min(end - cursor, sliceCap));
                if (slice < 8)
                {
                    Slate(end, block.Label);
                    break;
                }

                slots.Add(new Slot
                {
                    Start = cursor,
                    Duration = slice,
                    Path = current.Path,
                    FileOffset = played,
                    Title = current.Title,
                    Detail = current.Detail,
                    Block = block.Label,
                });
                cursor += slice;
                played += slice;
                remain -= slice;
                sinceBreak += slice;
            }

            if (cursor < end)
            {
                Slate(end, block.Label);
            }
        }

        if (cursor < Day)
        {
            Slate(Day, "Off the schedule");
        }

        return slots;
    }

    public static (Slot Slot, int Index, double Into) At(IReadOnlyList<Slot> slots, double seconds)
    {
        var time = ((seconds % Day) + Day) % Day;
        var lo = 0;
        var hi = slots.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var slot = slots[mid];
            if (time < slot.Start)
            {
                hi = mid - 1;
            }
            else if (time >= slot.Start + slot.Duration)
            {
                lo = mid + 1;
            }
            else
            {
                return (slot, mid, time - slot.Start);
            }
        }

        var last = slots.Count > 0 ? slots[^1] : new Slot { Start = 0, Duration = Day, Title = "Station" };
        return (last, Math.Max(0, slots.Count - 1), 0);
    }

    /// <summary>
    /// Files to play from this moment, wrapping the day, until about <paramref name="horizonSeconds"/> of output.
    /// </summary>
    public static List<(string Path, double InPoint, double Length, string Title)> Playback(IReadOnlyList<Slot> slots, double now, double horizonSeconds)
    {
        var list = new List<(string Path, double InPoint, double Length, string Title)>();
        if (slots.Count == 0)
        {
            return list;
        }

        var hit = At(slots, now);
        var index = hit.Index;
        var skip = hit.Into;
        var filled = 0d;
        var guard = 0;
        while (filled < horizonSeconds && guard < (slots.Count * 4))
        {
            var slot = slots[index];
            var length = slot.Duration - skip;
            if (!string.IsNullOrWhiteSpace(slot.Path) && length >= 5)
            {
                var take = Math.Min(length, horizonSeconds - filled);
                list.Add((slot.Path!, slot.FileOffset + skip, take, slot.Title));
                filled += take;
            }
            else if (length > 0)
            {
                filled += Math.Min(length, horizonSeconds - filled);
            }

            skip = 0;
            index++;
            if (index >= slots.Count)
            {
                index = 0;
            }

            guard++;
        }

        return list;
    }

    private static uint Hash(string text)
    {
        uint h = 2166136261;
        foreach (var c in text)
        {
            h ^= c;
            h *= 16777619;
        }

        return h;
    }

    private static Func<double> Rng(uint seed)
    {
        var a = seed;
        return () =>
        {
            a += 0x6D2B79F5;
            var t = a;
            t = (uint)((t ^ (t >> 15)) * (1 | t));
            t = (uint)((t + ((t ^ (t >> 7)) * (61 | t))) ^ t);
            return (t ^ (t >> 14)) / 4294967296d;
        };
    }

    private static List<T> Shuffle<T>(List<T> source, Func<double> rand)
    {
        var copy = source.ToList();
        for (var i = copy.Count - 1; i > 0; i--)
        {
            var j = (int)(rand() * (i + 1));
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy;
    }
}
