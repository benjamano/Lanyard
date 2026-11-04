using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.AudioClips;

/// <summary>
/// When an audio clip schedule plays, in UK wall-clock time. Pure, so the engine, the save check
/// and the "next plays" preview all agree.
/// </summary>
public static class AudioClipScheduleRules
{
    // A play that's due but missed (the server was down) still goes out if it's this late at most;
    // after that it's skipped, since a closing-time call an hour late is worse than none.
    public static readonly TimeSpan LateBy = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The times the schedule plays that belong to <paramref name="date"/>: the day an AtTime clip
    /// plays, or the day the location opens for a closing or opening time, so a closing call for
    /// Friday's 18:00-02:00 belongs to Friday even though it plays early on Saturday. A day with
    /// several opening windows gets one play per window. A day with no hours gets none.
    /// </summary>
    public static IEnumerable<DateTime> OccurrencesOn(AudioClipSchedule schedule, IEnumerable<LocationOpeningHours> hours, DateOnly date)
    {
        if (!AutomationScheduleDays.Matches(schedule.DaysOfWeek, date.DayOfWeek))
        {
            yield break;
        }

        if (schedule.Anchor == AudioClipScheduleAnchor.AtTime)
        {
            if (schedule.TimeOfDay is TimeOnly time)
            {
                yield return date.ToDateTime(time);
            }

            yield break;
        }

        TimeSpan offset = TimeSpan.FromMinutes(Math.Clamp(schedule.OffsetMinutes, 0, AudioClipSchedule.MaxOffsetMinutes));

        foreach (LocationOpeningHours window in hours
                     .Where(x => x.IsActive && x.DayOfWeek == date.DayOfWeek && OpeningHoursRules.IsValid(x.OpensAt, x.ClosesAt))
                     .OrderBy(x => x.OpensAt))
        {
            DateTime opens = date.ToDateTime(window.OpensAt);
            DateTime closes = window.ClosesAt <= window.OpensAt
                ? date.AddDays(1).ToDateTime(window.ClosesAt)
                : date.ToDateTime(window.ClosesAt);

            DateTime at = schedule.Anchor == AudioClipScheduleAnchor.BeforeClosing ? closes - offset : opens + offset;

            // A long offset on a short window would land outside it; keep it within opening hours.
            if (at < opens)
            {
                at = opens;
            }
            else if (at > closes)
            {
                at = closes;
            }

            yield return at;
        }
    }

    /// <summary>
    /// The occurrence due at <paramref name="venueLocal"/>, if any: one that has started and is at
    /// most <see cref="LateBy"/> old. Yesterday is checked too, for closes after midnight.
    /// </summary>
    public static DateTime? DueAt(AudioClipSchedule schedule, IEnumerable<LocationOpeningHours> hours, DateTime venueLocal)
    {
        List<LocationOpeningHours> windows = hours.ToList();
        DateOnly today = DateOnly.FromDateTime(venueLocal);

        return OccurrencesOn(schedule, windows, today.AddDays(-1))
            .Concat(OccurrencesOn(schedule, windows, today))
            .Where(x => x <= venueLocal && venueLocal - x < LateBy)
            .OrderByDescending(x => x)
            .Select(x => (DateTime?)x)
            .FirstOrDefault();
    }

    /// <summary>The next few plays from <paramref name="venueLocal"/>, for the preview.</summary>
    public static List<DateTime> Upcoming(AudioClipSchedule schedule, IEnumerable<LocationOpeningHours> hours, DateTime venueLocal, int count = 3)
    {
        List<LocationOpeningHours> windows = hours.ToList();
        DateOnly today = DateOnly.FromDateTime(venueLocal);

        // Two weeks ahead, so a once-a-week schedule still shows a few plays.
        return Enumerable.Range(-1, 16)
            .SelectMany(day => OccurrencesOn(schedule, windows, today.AddDays(day)))
            .Where(x => x >= venueLocal)
            .Distinct()
            .Order()
            .Take(count)
            .ToList();
    }

    // Identifies one play of one schedule. Includes the schedule's last edit, so changing the time
    // of a schedule that already played today lets it play again at the new time.
    public static string OccurrenceKey(AudioClipSchedule schedule, DateTime occurrence) =>
        $"{occurrence:yyyyMMddHHmm}:{(schedule.UpdateDate ?? schedule.CreateDate).Ticks}";

    /// <summary>A plain-English problem with the schedule, or null when it's fine to save.</summary>
    public static string? Validate(AudioClipSchedule schedule)
    {
        string name = schedule.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return "Give the schedule a name.";
        }

        if (name.Length > AudioClipSchedule.NameMaxLength)
        {
            return $"The name can be at most {AudioClipSchedule.NameMaxLength} characters.";
        }

        if (!AutomationScheduleDays.TryParse(schedule.DaysOfWeek, out List<DayOfWeek> days) || string.IsNullOrWhiteSpace(schedule.DaysOfWeek) || days.Count == 0)
        {
            return "Pick at least one day.";
        }

        return schedule.Anchor switch
        {
            AudioClipScheduleAnchor.AtTime when schedule.TimeOfDay is null => "Pick a time.",
            AudioClipScheduleAnchor.BeforeClosing or AudioClipScheduleAnchor.AfterOpening
                when schedule.OffsetMinutes is < 0 or > AudioClipSchedule.MaxOffsetMinutes
                => $"Pick between 0 and {AudioClipSchedule.MaxOffsetMinutes} minutes.",
            AudioClipScheduleAnchor.AtTime or AudioClipScheduleAnchor.BeforeClosing or AudioClipScheduleAnchor.AfterOpening => null,
            _ => "Pick when it plays."
        };
    }

    /// <summary>"15 minutes before closing, every day" and the like.</summary>
    public static string Describe(AudioClipSchedule schedule)
    {
        string when = schedule.Anchor switch
        {
            AudioClipScheduleAnchor.AtTime => $"At {schedule.TimeOfDay:HH\\:mm}",
            AudioClipScheduleAnchor.BeforeClosing => schedule.OffsetMinutes == 0 ? "At closing time" : $"{Minutes(schedule.OffsetMinutes)} before closing",
            _ => schedule.OffsetMinutes == 0 ? "At opening time" : $"{Minutes(schedule.OffsetMinutes)} after opening"
        };

        return $"{when}, {DescribeDays(schedule.DaysOfWeek)}";
    }

    private static string Minutes(int minutes) => minutes == 1 ? "1 minute" : $"{minutes} minutes";

    private static readonly DayOfWeek[] WeekOrder =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private static string DescribeDays(string? csv)
    {
        if (!AutomationScheduleDays.TryParse(csv, out List<DayOfWeek> days))
        {
            return "no days";
        }

        HashSet<DayOfWeek> set = [.. days];

        if (set.Count == 7)
        {
            return "every day";
        }

        if (set.SetEquals([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]))
        {
            return "weekdays";
        }

        if (set.SetEquals([DayOfWeek.Saturday, DayOfWeek.Sunday]))
        {
            return "weekends";
        }

        return string.Join(", ", WeekOrder.Where(set.Contains).Select(x => x.ToString()[..3]));
    }
}
