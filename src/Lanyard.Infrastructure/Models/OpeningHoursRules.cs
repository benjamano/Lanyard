namespace Lanyard.Infrastructure.Models;

/// <summary>
/// Whether a location is open at a venue-local moment, from its LocationOpeningHours rows. Shared by
/// the automation engine, the opening hours service's validation and the editor dialog.
/// </summary>
public static class OpeningHoursRules
{
    /// <summary>
    /// Open inside any active window: OpensAt inclusive, ClosesAt exclusive. A ClosesAt at or before
    /// OpensAt closes after midnight, so late-night windows are checked against yesterday's rows too:
    /// at 01:00 on Sunday a venue is still open on Saturday's 18:00-02:00.
    /// </summary>
    public static bool IsOpen(IEnumerable<LocationOpeningHours> hours, DateTime venueLocal)
    {
        TimeOnly now = TimeOnly.FromDateTime(venueLocal);
        DayOfWeek today = venueLocal.DayOfWeek;
        DayOfWeek yesterday = (DayOfWeek)(((int)today + 6) % 7);

        foreach (LocationOpeningHours day in hours.Where(h => h.IsActive && IsValid(h.OpensAt, h.ClosesAt)))
        {
            bool runsPastMidnight = day.ClosesAt <= day.OpensAt;

            if (day.DayOfWeek == today)
            {
                if (runsPastMidnight ? now >= day.OpensAt : now >= day.OpensAt && now < day.ClosesAt)
                {
                    return true;
                }
            }
            else if (day.DayOfWeek == yesterday && runsPastMidnight && now < day.ClosesAt)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Equal open and close times are ambiguous (open all day, or never), so they're refused.</summary>
    public static bool IsValid(TimeOnly opensAt, TimeOnly closesAt)
    {
        return opensAt != closesAt;
    }

    /// <summary>
    /// The first pair of windows that overlap, counting a past-midnight window's early hours on the
    /// next day, or null when none do. Equal times count as invalid, not as overlapping.
    /// </summary>
    public static (DayOfWeek Day, TimeOnly A, TimeOnly B)? FindOverlap(IEnumerable<(DayOfWeek Day, TimeOnly OpensAt, TimeOnly ClosesAt)> windows)
    {
        // Each window as minute ranges on the day it covers: a past-midnight window is split into
        // [opens, 24:00) on its own day and [00:00, closes) on the next.
        List<(DayOfWeek Day, int Start, int End, TimeOnly Label)> ranges = [];

        foreach ((DayOfWeek day, TimeOnly opensAt, TimeOnly closesAt) in windows.Where(w => IsValid(w.OpensAt, w.ClosesAt)))
        {
            int start = (int)opensAt.ToTimeSpan().TotalMinutes;
            int end = (int)closesAt.ToTimeSpan().TotalMinutes;

            if (end > start)
            {
                ranges.Add((day, start, end, opensAt));
            }
            else
            {
                ranges.Add((day, start, 24 * 60, opensAt));
                ranges.Add(((DayOfWeek)(((int)day + 1) % 7), 0, end, opensAt));
            }
        }

        foreach (IGrouping<DayOfWeek, (DayOfWeek Day, int Start, int End, TimeOnly Label)> day in ranges.GroupBy(r => r.Day))
        {
            List<(DayOfWeek Day, int Start, int End, TimeOnly Label)> sorted = [.. day.OrderBy(r => r.Start)];

            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Start < sorted[i - 1].End)
                {
                    return (day.Key, sorted[i - 1].Label, sorted[i].Label);
                }
            }
        }

        return null;
    }
}
