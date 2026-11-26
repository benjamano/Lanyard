namespace Lanyard.Infrastructure.Models;

/// <summary>
/// Whether a location is open at a venue-local moment, from its LocationOpeningHours rows and its
/// rota holidays (a Closed day shuts it, a bank holiday keeps the hours of the day it's staffed
/// like). Every opening-hours decision goes through <see cref="Resolve"/> or <see cref="IsOpen"/>:
/// automation, kiosk alerts, audio clips, screens, checklists and Insights.
/// </summary>
public static class OpeningHoursRules
{
    /// <summary>
    /// Whether the location is open at a venue-local moment, once its rota holidays are taken into
    /// account: open inside any of yesterday's or today's resolved windows (see <see cref="Resolve"/>).
    /// OpensAt is inclusive, ClosesAt exclusive. A window belongs to the day it opens on, so at 01:00
    /// on Sunday a venue is still open on Saturday's 18:00-02:00 even when Sunday is marked Closed,
    /// and shut when Saturday was. Pass no holidays where the location has none (or the feature is off).
    /// </summary>
    public static bool IsOpen(IEnumerable<LocationOpeningHours> hours, IEnumerable<RotaHoliday> holidays, DateTime venueLocal)
    {
        List<LocationOpeningHours> rows = hours as List<LocationOpeningHours> ?? [.. hours];
        List<RotaHoliday> days = holidays as List<RotaHoliday> ?? [.. holidays];
        DateOnly today = DateOnly.FromDateTime(venueLocal);

        return Resolve(rows, days, today.AddDays(-1)).IsOpenAt(venueLocal) || Resolve(rows, days, today).IsOpenAt(venueLocal);
    }

    /// <summary>
    /// A date's opening windows once holidays are applied. A Closed day has none. A bank holiday uses
    /// the windows of the day it's staffed like (Sunday unless the manager picked another). Any other
    /// day uses its own weekday's rows. Each window is placed on the date, closing the next morning
    /// when ClosesAt is at or before OpensAt.
    /// </summary>
    public static OpeningDay Resolve(IEnumerable<LocationOpeningHours> hours, IEnumerable<RotaHoliday> holidays, DateOnly date) =>
        Resolve(hours, RotaHolidayRules.For(holidays, date));

    /// <summary>The same, for a day whose holidays are already worked out (RotaHolidayRules.For).</summary>
    public static OpeningDay Resolve(IEnumerable<LocationOpeningHours> hours, RotaHolidayDay holiday)
    {
        DateOnly date = holiday.Day;

        if (holiday.IsClosed)
        {
            return new OpeningDay(date, [], holiday);
        }

        DayOfWeek like = holiday.StaffedLike;

        List<OpeningWindow> windows = hours
            .Where(h => h.IsActive && h.DayOfWeek == like && IsValid(h.OpensAt, h.ClosesAt))
            .OrderBy(h => h.OpensAt)
            .Select(h => new OpeningWindow(
                date.ToDateTime(h.OpensAt),
                (h.ClosesAt <= h.OpensAt ? date.AddDays(1) : date).ToDateTime(h.ClosesAt)))
            .ToList();

        return new OpeningDay(date, windows, holiday);
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

/// <summary>One opening window placed on a real date, in venue-local time. ClosesLocal is exclusive.</summary>
public sealed record OpeningWindow(DateTime OpensLocal, DateTime ClosesLocal)
{
    public bool Contains(DateTime venueLocal) => venueLocal >= OpensLocal && venueLocal < ClosesLocal;
}

/// <summary>
/// A location's opening hours for one date, with its holidays applied (OpeningHoursRules.Resolve).
/// No windows means shut that day: a Closed holiday, or no hours on the weekday it follows.
/// </summary>
public sealed record OpeningDay(DateOnly Date, IReadOnlyList<OpeningWindow> Windows, RotaHolidayDay Holiday)
{
    public bool IsClosedForHoliday => Holiday.IsClosed;

    public bool IsOpenAt(DateTime venueLocal) => Windows.Any(w => w.Contains(venueLocal));
}
