using Lanyard.Infrastructure.Models;
using System.Globalization;

namespace Lanyard.Application.Services.MusicScheduling;

/// <summary>
/// The rules for a location's music schedule, kept free of I/O so the editor, the service and the
/// tests all agree. Times are UK wall-clock; an end at or before the start runs into the next day.
/// </summary>
public static class MusicDaypartRules
{
    private const int MinutesPerDay = 24 * 60;
    private const int MinutesPerWeek = 7 * MinutesPerDay;

    /// <summary>
    /// Checks one daypart on its own. Returns the problem to show the user, or null when it's fine.
    /// </summary>
    public static string? Validate(MusicDaypart daypart)
    {
        if (string.IsNullOrWhiteSpace(daypart.Name))
        {
            return "Give this part of the day a name.";
        }

        if (daypart.Name.Trim().Length > MusicDaypart.NameMaxLength)
        {
            return $"The name can be at most {MusicDaypart.NameMaxLength} characters.";
        }

        // Blank parses as "every day" for automation rules, but a daypart always stores its days.
        if (string.IsNullOrWhiteSpace(daypart.DaysOfWeek) || !AutomationScheduleDays.TryParse(daypart.DaysOfWeek, out _))
        {
            return "Pick at least one day.";
        }

        if (daypart.StartTime == daypart.EndTime)
        {
            return "The start and end can't be the same time.";
        }

        if (daypart.Volume is int volume && (volume < 0 || volume > 100))
        {
            return "Volume must be between 0 and 100.";
        }

        return null;
    }

    /// <summary>
    /// Finds the first daypart in <paramref name="others"/> that shares any minute of the week with
    /// <paramref name="candidate"/>, including across midnight and from Saturday night into Sunday.
    /// Returns a message naming it, or null when nothing overlaps.
    /// </summary>
    public static string? FindOverlap(MusicDaypart candidate, IEnumerable<MusicDaypart> others)
    {
        List<(int Start, int End)> candidateMinutes = WeekIntervals(candidate).ToList();

        foreach (MusicDaypart other in others.Where(x => x.Id != candidate.Id))
        {
            foreach ((int otherStart, int otherEnd) in WeekIntervals(other))
            {
                foreach ((int start, int end) in candidateMinutes)
                {
                    int overlapStart = Math.Max(start, otherStart);

                    if (overlapStart < Math.Min(end, otherEnd))
                    {
                        return $"This overlaps \"{other.Name}\" on {DescribeWeekMinute(overlapStart)}. Change the times or days so they don't overlap.";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The minutes of the week (Sunday 00:00 = 0) a daypart covers, split in two where one starting
    /// late on Saturday runs past the end of the week.
    /// </summary>
    internal static IEnumerable<(int Start, int End)> WeekIntervals(MusicDaypart daypart)
    {
        if (!AutomationScheduleDays.TryParse(daypart.DaysOfWeek, out List<DayOfWeek> days) || daypart.StartTime == daypart.EndTime)
        {
            yield break;
        }

        int startOfDay = MinuteOfDay(daypart.StartTime);
        int endOfDay = MinuteOfDay(daypart.EndTime);
        int length = daypart.EndsNextDay ? MinutesPerDay - startOfDay + endOfDay : endOfDay - startOfDay;

        foreach (DayOfWeek day in days.Distinct())
        {
            int start = (int)day * MinutesPerDay + startOfDay;
            int end = start + length;

            if (end <= MinutesPerWeek)
            {
                yield return (start, end);
            }
            else
            {
                yield return (start, MinutesPerWeek);
                yield return (0, end - MinutesPerWeek);
            }
        }
    }

    /// <summary>
    /// "Mon, Tue, Wed" style list of a daypart's days, Monday first as on the schedule page.
    /// </summary>
    public static string DescribeDays(string? daysOfWeek)
    {
        if (!AutomationScheduleDays.TryParse(daysOfWeek, out List<DayOfWeek> days))
        {
            return "No days";
        }

        if (days.Distinct().Count() == 7)
        {
            return "Every day";
        }

        return string.Join(", ", WeekOrder.Where(days.Contains).Select(d => d.ToString()[..3]));
    }

    /// <summary>
    /// "08:00–10:00", or "22:00–02:00 (next day)" when it runs past midnight.
    /// </summary>
    public static string DescribeTimes(MusicDaypart daypart)
    {
        string times = $"{FormatTime(daypart.StartTime)}–{FormatTime(daypart.EndTime)}";

        return daypart.EndsNextDay ? $"{times} (next day)" : times;
    }

    public static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Monday-first, matching the rota's week.
    /// </summary>
    public static IReadOnlyList<DayOfWeek> WeekOrder { get; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    private static int MinuteOfDay(TimeOnly time) => time.Hour * 60 + time.Minute;

    private static string DescribeWeekMinute(int minuteOfWeek)
    {
        DayOfWeek day = (DayOfWeek)(minuteOfWeek / MinutesPerDay);
        int minuteOfDay = minuteOfWeek % MinutesPerDay;

        return $"{day} at {FormatTime(new TimeOnly(minuteOfDay / 60, minuteOfDay % 60))}";
    }
}
