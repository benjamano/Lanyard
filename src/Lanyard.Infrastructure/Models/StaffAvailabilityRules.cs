using System.Globalization;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models;

/// <summary>
/// What someone's StaffAvailability rows say about a shift. Pure, shared by the availability
/// service's validation, the rota's save warning and the shift suggestions.
/// </summary>
public static class StaffAvailabilityRules
{
    private const int MinutesPerDay = 24 * 60;
    private const int MinutesPerWeek = 7 * MinutesPerDay;

    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>Whether the row's weekly window starts on this venue-local day.</summary>
    public static bool AppliesOn(StaffAvailability row, DateOnly day) =>
        row.IsActive
        && day.DayOfWeek == row.DayOfWeek
        && (row.EffectiveFrom is not DateOnly from || day >= from)
        && (row.EffectiveTo is not DateOnly to || day <= to);

    /// <summary>
    /// The row's window starting on this day, in UTC. Converted through the venue's zone, so a
    /// window on a clock-change day is the real length (an all-day window is 23 or 25 hours).
    /// </summary>
    public static (DateTime StartUtc, DateTime EndUtc) WindowOn(StaffAvailability row, DateOnly day)
    {
        if (row.IsAllDay)
        {
            return (RotaTime.StartOfDayUtc(day), RotaTime.StartOfDayUtc(day.AddDays(1)));
        }

        DateTime start = RotaTime.ToUtc(day, row.StartTime);
        DateTime end = row.EndTime <= row.StartTime
            ? RotaTime.ToUtc(day.AddDays(1), row.EndTime)
            : RotaTime.ToUtc(day, row.EndTime);

        return (start, end);
    }

    /// <summary>
    /// Every window that overlaps [startUtc, endUtc). A window from the day before is included,
    /// since one that runs past midnight can reach into the shift.
    /// </summary>
    public static List<(StaffAvailability Row, DateTime StartUtc, DateTime EndUtc)> WindowsOverlapping(
        IEnumerable<StaffAvailability> rows, DateTime startUtc, DateTime endUtc)
    {
        List<StaffAvailability> list = rows.ToList();
        List<(StaffAvailability, DateTime, DateTime)> windows = [];

        DateOnly first = RotaTime.LocalDate(startUtc).AddDays(-1);
        DateOnly last = RotaTime.LocalDate(endUtc);

        for (DateOnly day = first; day <= last; day = day.AddDays(1))
        {
            foreach (StaffAvailability row in list.Where(x => AppliesOn(x, day)))
            {
                (DateTime windowStart, DateTime windowEnd) = WindowOn(row, day);

                if (windowStart < endUtc && windowEnd > startUtc)
                {
                    windows.Add((row, windowStart, windowEnd));
                }
            }
        }

        return windows;
    }

    /// <summary>The first Unavailable window touching any part of the shift, or null.</summary>
    public static StaffAvailability? Conflict(IEnumerable<StaffAvailability> rows, DateTime startUtc, DateTime endUtc) =>
        WindowsOverlapping(rows, startUtc, endUtc)
            .Where(x => x.Row.Kind == AvailabilityKind.Unavailable)
            .OrderBy(x => x.StartUtc)
            .Select(x => x.Row)
            .FirstOrDefault();

    /// <summary>The Preferred window covering the whole shift, or null.</summary>
    public static StaffAvailability? Preferred(IEnumerable<StaffAvailability> rows, DateTime startUtc, DateTime endUtc) =>
        WindowsOverlapping(rows, startUtc, endUtc)
            .Where(x => x.Row.Kind == AvailabilityKind.Preferred && x.StartUtc <= startUtc && x.EndUtc >= endUtc)
            .Select(x => x.Row)
            .FirstOrDefault();

    /// <summary>"Tuesdays, 17:00–23:00", "Sundays, all day", "Fridays, 22:00–02:00 from 5 Jan 2027".</summary>
    public static string Describe(StaffAvailability row)
    {
        string day = $"{Uk.DateTimeFormat.GetDayName(row.DayOfWeek)}s";
        string time = row.IsAllDay
            ? "all day"
            : $"{row.StartTime.ToString("HH:mm", Uk)}–{row.EndTime.ToString("HH:mm", Uk)}";

        string dates = (row.EffectiveFrom, row.EffectiveTo) switch
        {
            (DateOnly from, DateOnly to) => $" from {Date(from)} to {Date(to)}",
            (DateOnly from, null) => $" from {Date(from)}",
            (null, DateOnly to) => $" until {Date(to)}",
            _ => string.Empty
        };

        return $"{day}, {time}{dates}";
    }

    /// <summary>
    /// Two active windows that overlap on the same day while both are in effect, described for
    /// the person, or null when none do. A past-midnight window's early hours count on the next day.
    /// </summary>
    public static string? FindOverlap(IEnumerable<StaffAvailability> rows)
    {
        List<StaffAvailability> active = rows.Where(x => x.IsActive).ToList();

        for (int i = 0; i < active.Count; i++)
        {
            for (int j = i + 1; j < active.Count; j++)
            {
                if (DatesOverlap(active[i], active[j]) && TimesOverlap(active[i], active[j]))
                {
                    return $"{Describe(active[i])} overlaps {Describe(active[j])}.";
                }
            }
        }

        return null;
    }

    /// <summary>Why a window can't be saved, or null if it can.</summary>
    public static string? Validate(StaffAvailability row) =>
        row.EffectiveFrom is DateOnly from && row.EffectiveTo is DateOnly to && to < from
            ? "The end date must not be before the start date."
            : row.Notes?.Length > 200
                ? "Keep the note to 200 characters."
                : null;

    private static bool DatesOverlap(StaffAvailability a, StaffAvailability b) =>
        (a.EffectiveTo is not DateOnly aTo || b.EffectiveFrom is not DateOnly bFrom || bFrom <= aTo)
        && (b.EffectiveTo is not DateOnly bTo || a.EffectiveFrom is not DateOnly aFrom || aFrom <= bTo);

    private static bool TimesOverlap(StaffAvailability a, StaffAvailability b) =>
        WeekRanges(a).Any(x => WeekRanges(b).Any(y => x.Start < y.End && y.Start < x.End));

    // The window as minute ranges in a Monday-starting week; one that runs from Sunday night into
    // Monday is split at the end of the week.
    private static IEnumerable<(int Start, int End)> WeekRanges(StaffAvailability row)
    {
        int dayStart = ((int)row.DayOfWeek + 6) % 7 * MinutesPerDay;
        int start = dayStart + (row.IsAllDay ? 0 : (int)row.StartTime.ToTimeSpan().TotalMinutes);
        int end = row.IsAllDay
            ? dayStart + MinutesPerDay
            : dayStart + (int)row.EndTime.ToTimeSpan().TotalMinutes + (row.EndTime <= row.StartTime ? MinutesPerDay : 0);

        if (end <= MinutesPerWeek)
        {
            yield return (start, end);
            yield break;
        }

        yield return (start, MinutesPerWeek);
        yield return (0, end - MinutesPerWeek);
    }

    private static string Date(DateOnly date) => date.ToString("d MMM yyyy", Uk);
}
