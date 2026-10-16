using Lanyard.Infrastructure.DTO.Insights;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Application.Services.MusicScheduling;

namespace Lanyard.Application.Services.Insights;

// A finished game: when the server recorded it (the end of the game), its configured length, and
// how many guns scored.
public readonly record struct GameFact(DateTime PlayedAtUtc, int DurationSeconds, int Players);

// Someone on site from StartUtc to EndUtc. Weight scales the time down, so a rota shift's break
// is spread across it and its hours still add up to Shift.PaidHours.
public readonly record struct WorkSpan(DateTime StartUtc, DateTime EndUtc, decimal Weight = 1m);

// The demand-and-staffing sums, kept free of I/O so the service, the export and the tests agree.
//
// Every bucket is a venue-local hour (RotaTime). The UK only ever moves its clocks by a whole
// hour, so a UTC hour always lines up with exactly one local hour: time is split on UTC hour
// boundaries and each piece is labelled with the local date and hour it falls in. When the clocks
// go back, the repeated 01:00 hour gets both real hours added together; when they go forward,
// 01:00 simply doesn't exist that night.
public static class InsightsRules
{
    // A weekday-hour needs this many occurrences in the range before it's judged at all.
    public const int MinCellSamples = 3;

    // ...and this many games before it counts towards the typical rate, or can be called busy.
    public const int MinCellGames = 5;

    // No flags at all for a range shorter than this, or with fewer games than this: one party or
    // one quiet week would otherwise set what "typical" means.
    public const int MinFlagRangeDays = 14;
    public const int MinFlagGames = 30;

    // Busy: at least half as many games again per staff-hour as typical. Quiet: half or fewer.
    public const decimal UnderstaffedRatio = 1.5m;
    public const decimal OverstaffedRatio = 0.5m;

    // Only call an hour overstaffed if someone could go home and someone would still be there.
    public const decimal MinAverageStaffToCallOverstaffed = 1.5m;

    // A game's DurationSeconds is its configured length; anything longer is a bad snapshot.
    public static readonly TimeSpan MaxGameLength = TimeSpan.FromHours(2);

    private readonly record struct SlotKey(DateOnly Date, int Hour);

    private sealed class SlotTotals
    {
        public int Games;
        public int Players;
        public decimal Clocked;
        public decimal Rota;
    }

    // The venue-local hour a UTC instant falls in.
    public static (DateOnly Date, int Hour) LocalHour(DateTime utc)
    {
        DateTime local = RotaTime.ToVenueLocal(utc);
        return (DateOnly.FromDateTime(local), local.Hour);
    }

    // Splits [startUtc, endUtc) at each hour boundary, giving the start of each UTC hour it touches
    // and how many hours of it are covered (0 < hours <= 1).
    public static IEnumerable<(DateTime HourStartUtc, decimal Hours)> SplitIntoHours(DateTime startUtc, DateTime endUtc)
    {
        if (endUtc <= startUtc)
        {
            yield break;
        }

        DateTime hourStart = new(startUtc.Year, startUtc.Month, startUtc.Day, startUtc.Hour, 0, 0, DateTimeKind.Utc);

        while (hourStart < endUtc)
        {
            DateTime hourEnd = hourStart.AddHours(1);
            DateTime from = startUtc > hourStart ? startUtc : hourStart;
            DateTime to = endUtc < hourEnd ? endUtc : hourEnd;

            if (to > from)
            {
                yield return (hourStart, (decimal)(to - from).TotalMinutes / 60m);
            }

            hourStart = hourEnd;
        }
    }

    // The game started its configured length before the server recorded it.
    public static DateTime EstimatedStartUtc(GameFact game)
    {
        TimeSpan length = TimeSpan.FromSeconds(Math.Max(0, game.DurationSeconds));
        return game.PlayedAtUtc - (length > MaxGameLength ? MaxGameLength : length);
    }

    // An open entry counts until now, but never past the point the clock-in service treats as
    // someone who forgot to clock out.
    public static DateTime EffectiveClockOutUtc(DateTime clockInUtc, DateTime? clockOutUtc, DateTime nowUtc, TimeSpan staleAfter)
    {
        if (clockOutUtc is DateTime outUtc)
        {
            return outUtc;
        }

        DateTime cap = clockInUtc + staleAfter;
        return nowUtc < cap ? nowUtc : cap;
    }

    // A shift's break isn't placed at any time of day, so it's spread across the whole shift.
    public static WorkSpan RotaSpan(DateTime startUtc, DateTime endUtc, int breakMinutes)
    {
        decimal minutes = (decimal)(endUtc - startUtc).TotalMinutes;
        decimal weight = minutes <= 0 ? 0m : Math.Max(0m, minutes - breakMinutes) / minutes;
        return new WorkSpan(startUtc, endUtc, weight);
    }

    public static decimal? Median(IEnumerable<decimal> values)
    {
        List<decimal> sorted = [.. values.OrderBy(x => x)];

        if (sorted.Count == 0)
        {
            return null;
        }

        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2m;
    }

    // Counts towards the location's typical games per staff-hour.
    public static bool IsTypicalRateSample(InsightsCell cell) =>
        cell.Samples >= MinCellSamples && cell.Games >= MinCellGames && cell.ClockedHours > 0;

    public static StaffingFlag Flag(InsightsCell cell, decimal? median)
    {
        if (cell.Samples < MinCellSamples)
        {
            return StaffingFlag.InsufficientData;
        }

        if (cell.GamesPerStaffHour is not decimal rate)
        {
            // Games with nobody clocked in at all.
            return cell.Games >= MinCellGames ? StaffingFlag.Understaffed : StaffingFlag.None;
        }

        if (median is not decimal typical || typical <= 0)
        {
            return StaffingFlag.None;
        }

        if (cell.Games >= MinCellGames && rate >= typical * UnderstaffedRatio)
        {
            return StaffingFlag.Understaffed;
        }

        if (rate <= typical * OverstaffedRatio && cell.AverageStaff >= MinAverageStaffToCallOverstaffed)
        {
            return StaffingFlag.Overstaffed;
        }

        return StaffingFlag.None;
    }

    public static InsightsFigures Calculate(
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<GameFact> games,
        IReadOnlyCollection<WorkSpan> clocked,
        IReadOnlyCollection<WorkSpan> rota,
        IReadOnlyCollection<LocationOpeningHours> openingHours,
        IReadOnlyCollection<MusicDaypart> dayparts)
    {
        DateTime rangeStartUtc = RotaTime.StartOfDayUtc(from);
        DateTime rangeEndUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

        // Every local hour in the range, in order, so closed and quiet hours still count as
        // occurrences of their weekday-hour.
        Dictionary<SlotKey, SlotTotals> totals = [];
        List<SlotKey> order = [];

        for (DateTime hour = rangeStartUtc; hour < rangeEndUtc; hour = hour.AddHours(1))
        {
            (DateOnly date, int localHour) = LocalHour(hour);
            SlotKey key = new(date, localHour);

            if (totals.TryAdd(key, new SlotTotals()))
            {
                order.Add(key);
            }
        }

        foreach (GameFact game in games)
        {
            DateTime start = EstimatedStartUtc(game);

            if (start < rangeStartUtc || start >= rangeEndUtc)
            {
                continue;
            }

            (DateOnly date, int hour) = LocalHour(start);
            SlotTotals slot = totals[new SlotKey(date, hour)];
            slot.Games++;
            slot.Players += game.Players;
        }

        AddSpans(totals, clocked, rangeStartUtc, rangeEndUtc, (slot, hours) => slot.Clocked += hours);
        AddSpans(totals, rota, rangeStartUtc, rangeEndUtc, (slot, hours) => slot.Rota += hours);

        List<LocationOpeningHours> activeHours = [.. openingHours.Where(x => x.IsActive)];
        bool hasOpeningHours = activeHours.Count > 0;
        List<MusicDaypart> activeDayparts = [.. dayparts.Where(x => x.IsActive)];

        List<InsightsSlot> slots = [];

        // Kept unrounded so the totals add up exactly; the page and the export round for display.
        foreach (SlotKey key in order)
        {
            SlotTotals t = totals[key];
            DateTime local = key.Date.ToDateTime(new TimeOnly(key.Hour, 0));
            bool hasActivity = t.Games > 0 || t.Clocked > 0 || t.Rota > 0;

            // With no opening hours set, any hour something happened in is taken as open.
            bool isOpen = hasOpeningHours ? OpeningHoursRules.IsOpen(activeHours, local) : hasActivity;
            string? daypart = activeDayparts.Count > 0 ? MusicDaypartRules.Resolve(activeDayparts, local)?.Daypart.Name : null;

            slots.Add(new InsightsSlot(key.Date, key.Hour, isOpen, daypart, t.Games, t.Players, t.Clocked, t.Rota));
        }

        List<InsightsCell> rawCells = [.. slots
            .Where(x => x.IsOpen)
            .GroupBy(x => (x.Date.DayOfWeek, x.Hour))
            .Select(g => new InsightsCell(
                g.Key.DayOfWeek,
                g.Key.Hour,
                g.Count(),
                g.Sum(x => x.Games),
                g.Sum(x => x.Players),
                g.Sum(x => x.ClockedHours),
                g.Sum(x => x.RotaHours),
                StaffingFlag.None))
            .OrderBy(x => ((int)x.Day + 6) % 7)
            .ThenBy(x => x.Hour)];

        InsightsTotals summaryTotals = new(
            slots.Sum(x => x.Games),
            slots.Sum(x => x.Players),
            slots.Sum(x => x.ClockedHours),
            slots.Sum(x => x.RotaHours),
            slots.Where(x => x.IsOpen).Sum(x => x.ClockedHours));

        decimal? median = Median(rawCells.Where(IsTypicalRateSample).Select(x => x.GamesPerStaffHour!.Value));
        int days = to.DayNumber - from.DayNumber + 1;

        string? reason = days < MinFlagRangeDays
            ? "Pick at least two weeks to see staffing flags."
            : summaryTotals.Games < MinFlagGames
                ? $"Staffing flags need at least {MinFlagGames} games in the range."
                : summaryTotals.ClockedHoursWhileOpen <= 0
                    ? "Staffing flags need clocked hours from timesheets."
                    : median is null
                        ? "No hour has enough games and clocked time yet to show what's typical."
                        : null;

        bool flagsAvailable = reason is null;

        List<InsightsCell> cells = [.. rawCells.Select(x => x with
        {
            Flag = flagsAvailable ? Flag(x, median) : StaffingFlag.InsufficientData
        })];

        List<InsightsDay> dayRows = [.. slots
            .GroupBy(x => x.Date)
            .Select(g => new InsightsDay(
                g.Key,
                g.Sum(x => x.Games),
                g.Sum(x => x.Players),
                g.Sum(x => x.ClockedHours),
                g.Sum(x => x.RotaHours),
                g.Count(x => x.IsOpen)))
            .OrderBy(x => x.Date)];

        List<InsightsDaypartRow> daypartRows = [.. slots
            .Where(x => x.Daypart is not null && (x.IsOpen || x.HasActivity))
            .GroupBy(x => x.Daypart!)
            .Select(g => new InsightsDaypartRow(
                g.Key,
                g.Sum(x => x.Games),
                g.Sum(x => x.Players),
                g.Sum(x => x.ClockedHours),
                g.Sum(x => x.RotaHours)))
            .OrderByDescending(x => x.Games)
            .ThenBy(x => x.Name)];

        return new InsightsFigures(
            summaryTotals,
            [.. slots.Where(x => x.IsOpen || x.HasActivity)],
            cells,
            dayRows,
            daypartRows,
            median,
            flagsAvailable,
            reason);
    }

    private static void AddSpans(Dictionary<SlotKey, SlotTotals> totals, IEnumerable<WorkSpan> spans,
        DateTime rangeStartUtc, DateTime rangeEndUtc, Action<SlotTotals, decimal> add)
    {
        foreach (WorkSpan span in spans)
        {
            DateTime start = span.StartUtc < rangeStartUtc ? rangeStartUtc : span.StartUtc;
            DateTime end = span.EndUtc > rangeEndUtc ? rangeEndUtc : span.EndUtc;

            foreach ((DateTime hourStart, decimal hours) in SplitIntoHours(start, end))
            {
                (DateOnly date, int hour) = LocalHour(hourStart);
                add(totals[new SlotKey(date, hour)], hours * span.Weight);
            }
        }
    }
}
