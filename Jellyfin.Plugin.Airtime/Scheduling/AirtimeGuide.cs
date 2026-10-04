using System.Globalization;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.Plugin.Airtime.Scheduling;

/// <summary>
/// Turns a day's lineup into the titles and pictures the guide and the channel folder show.
/// </summary>
public static class AirtimeGuide
{
    public static string ChannelKey(string channelId) => "airtime-" + channelId;

    public static (Slot Now, string? Next) OnNow(IReadOnlyList<Slot> slots, double now)
    {
        if (slots.Count == 0)
        {
            return (new Slot { Title = "Airtime", Duration = ScheduleBuilder.Day }, null);
        }

        var hit = ScheduleBuilder.At(slots, now);
        var current = ProgramSlot(slots, hit.Index, hit.Slot);
        string? next = null;
        for (var step = 1; step <= slots.Count; step++)
        {
            var slot = slots[(hit.Index + step) % slots.Count];
            if (!IsProgram(slot) || string.Equals(slot.Title, current.Title, StringComparison.Ordinal))
            {
                continue;
            }

            next = slot.Title;
            break;
        }

        return (current, next);
    }

    public static List<ProgramInfo> Programs(string channelKey, IReadOnlyList<Slot> slots, DateOnly day, DateTime rangeStartUtc, DateTime rangeEndUtc)
    {
        var list = new List<ProgramInfo>();
        var midnight = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local);
        ProgramInfo? open = null;
        var openEnd = 0d;
        string? openTitle = null;

        void Flush()
        {
            if (open is null)
            {
                return;
            }

            var end = midnight.AddSeconds(openEnd).ToUniversalTime();
            if (end > rangeStartUtc && open.StartDate < rangeEndUtc && (end - open.StartDate).TotalSeconds >= 20)
            {
                open.EndDate = end;
                list.Add(open);
            }

            open = null;
            openTitle = null;
        }

        foreach (var slot in slots)
        {
            if (!IsProgram(slot))
            {
                // A break stays inside the show, so the guide does not fill with commercials.
                if (open is not null && slot.IsSpot && slot.Start <= openEnd + 2)
                {
                    openEnd = slot.Start + slot.Duration;
                }

                continue;
            }

            var title = string.IsNullOrWhiteSpace(slot.Title) ? "Airtime" : slot.Title;
            if (open is not null && string.Equals(title, openTitle, StringComparison.Ordinal) && slot.Start <= openEnd + 2)
            {
                openEnd = slot.Start + slot.Duration;
                if (string.IsNullOrWhiteSpace(open.ImagePath) && !string.IsNullOrWhiteSpace(slot.Image))
                {
                    open.ImagePath = slot.Image;
                    open.HasImage = true;
                }

                continue;
            }

            Flush();
            openTitle = title;
            openEnd = slot.Start + slot.Duration;
            open = new ProgramInfo
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"{channelKey}-{day:yyyyMMdd}-{(int)slot.Start}"),
                ChannelId = channelKey,
                Name = title,
                EpisodeTitle = string.IsNullOrWhiteSpace(slot.Detail) ? null : slot.Detail,
                Overview = string.IsNullOrWhiteSpace(slot.Detail) ? null : slot.Detail,
                StartDate = midnight.AddSeconds(slot.Start).ToUniversalTime(),
                ImagePath = string.IsNullOrWhiteSpace(slot.Image) ? null : slot.Image,
                HasImage = !string.IsNullOrWhiteSpace(slot.Image),
                IsSeries = !string.IsNullOrWhiteSpace(slot.Detail),
                IsMovie = string.IsNullOrWhiteSpace(slot.Detail),
            };
        }

        Flush();
        return list;
    }

    private static Slot ProgramSlot(IReadOnlyList<Slot> slots, int index, Slot current)
    {
        if (IsProgram(current) || !current.IsSpot)
        {
            return current;
        }

        for (var step = 1; step <= slots.Count; step++)
        {
            var slot = slots[(index - step + slots.Count) % slots.Count];
            if (IsProgram(slot))
            {
                return slot;
            }

            if (!slot.IsSpot)
            {
                break;
            }
        }

        return current;
    }

    private static bool IsProgram(Slot slot)
    {
        return !slot.IsSpot
            && !string.IsNullOrWhiteSpace(slot.Path)
            && !string.Equals(slot.Title, "Station hold", StringComparison.Ordinal);
    }
}
