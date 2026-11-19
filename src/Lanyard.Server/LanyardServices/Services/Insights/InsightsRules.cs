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

// A booked party, reduced to what demand needs: its times, what kind of hire it is, and how many
// children are coming. Deliberately no strings - no child, contact, allergy or price can reach
// insights or its export through this.
public readonly record struct PartyFact(
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime? LaserTagTimeUtc,
    PrivateHireKind? HireKind,
    int ExpectedChildren);

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

    // A party's laser tag is booked for one time, but nothing records which games it played. Games
    // starting from a little before that time (the group is briefed and suited early) until 40
    // minutes after it (two back-to-back games) are put down to the party.
    public static readonly TimeSpan PartyLaserLeadIn = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PartyLaserWindow = TimeSpan.FromMinutes(40);

    private readonly record struct SlotKey(DateOnly Date, int Hour);

    private sealed class SlotTotals
    {
        public int Games;
        public int PartyGames;
        public int Players;
        public decimal Clocked;
        public decimal Rota;
        public int Parties;
        public int ExpectedChildren;
    }

    private sealed class DayTotals
    {
        public int Parties;
        public int ExpectedChildren;
    }

    // When a party's games are expected, or null when none can be told apart from walk-ins:
    //  1. its own laser tag time;
    //  2. a laser tag hire has the arena for the whole booking, so every game in it is the hire's;
    //  3. a kids' party with no time (Just Step imports never have one) uses the location's default
    //     "laser tag starts N minutes after arrival" from Party settings;
    //  4. otherwise none. A play-only hire plays no laser tag at all.
    // Known misses, all accepted for an aggregate: walk-ins playing inside a party's window count as
    // the party's; a party running over half an hour late, or playing a third game, counts as
    // walk-ins; the default offset is applied to kids' parties that don't play laser tag too; and a
    // location with two arenas counts both arenas' games in the window.
    public static (DateTime StartUtc, DateTime EndUtc)? LaserWindow(PartyFact party, int? laserTagOffsetMinutes)
    {
        if (party.LaserTagTimeUtc is DateTime laser)
        {
            return AroundLaserTime(laser);
        }

        if (party.HireKind is PrivateHireKind.LaserTagOnly or PrivateHireKind.LaserTagAndPlay)
        {
            return party.EndUtc > party.StartUtc ? (party.StartUtc, party.EndUtc) : null;
        }

        if (party.HireKind is null && laserTagOffsetMinutes is int offset)
        {
            return AroundLaserTime(party.StartUtc.AddMinutes(offset));
        }

        return null;
    }

    // A kids' party whose games can't be found: no laser tag time and no default to go on.
    public static bool IsMissingLaserTime(PartyFact party, int? laserTagOffsetMinutes) =>
        party.HireKind is null && LaserWindow(party, laserTagOffsetMinutes) is null;

    private static (DateTime StartUtc, DateTime EndUtc) AroundLaserTime(DateTime laserUtc) =>
        (laserUtc - PartyLaserLeadIn, laserUtc + PartyLaserWindow);

    private static DateTime HourStart(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

    // Windows filed under each UTC hour they touch, so a game is only checked against the few
    // windows near it rather than every party in the range.
    private static Dictionary<DateTime, List<(DateTime StartUtc, DateTime EndUtc)>> IndexWindows(
        IEnumerable<(DateTime StartUtc, DateTime EndUtc)> windows)
    {
        Dictionary<DateTime, List<(DateTime, DateTime)>> byHour = [];

        foreach ((DateTime start, DateTime end) window in windows)
        {
            foreach ((DateTime hourStart, _) in SplitIntoHours(window.start, window.end))
            {
                if (!byHour.TryGetValue(hourStart, out List<(DateTime, DateTime)>? list))
                {
                    byHour[hourStart] = list = [];
                }

                list.Add(window);
            }
        }

        return byHour;
    }

    // A game in two parties' windows still counts once.
    private static bool IsInAnyWindow(Dictionary<DateTime, List<(DateTime StartUtc, DateTime EndUtc)>> byHour, DateTime startUtc) =>
        byHour.TryGetValue(HourStart(startUtc), out List<(DateTime StartUtc, DateTime EndUtc)>? windows)
        && windows.Any(w => startUtc >= w.StartUtc && startUtc < w.EndUtc);

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

    // Counts towards the location's typical games per staff-hour. Party hours never do.
    public static bool IsTypicalRateSample(InsightsCell cell) =>
        cell.RateSamples >= MinCellSamples && cell.RateGames >= MinCellGames && cell.GamesPerStaffHour is not null;

    // Most of this hour's occurrences had a party on, so there's too little normal trade to judge.
    public static bool IsMostlyParties(InsightsCell cell) =>
        cell.Samples >= MinCellSamples && cell.PartySamples > 0 && cell.PartySamples * 2 >= cell.Samples;

    public static StaffingFlag Flag(InsightsCell cell, decimal? median)
    {
        if (cell.Samples < MinCellSamples)
        {
            return StaffingFlag.InsufficientData;
        }

        if (IsMostlyParties(cell))
        {
            return StaffingFlag.PartyHours;
        }

        if (cell.RateSamples < MinCellSamples)
        {
            return StaffingFlag.InsufficientData;
        }

        if (cell.GamesPerStaffHour is not decimal rate)
        {
            // Games with nobody (or barely anybody) clocked in.
            return cell.RateGames >= MinCellGames ? StaffingFlag.Understaffed : StaffingFlag.None;
        }

        if (median is not decimal typical || typical <= 0)
        {
            return StaffingFlag.None;
        }

        if (cell.RateGames >= MinCellGames && rate >= typical * UnderstaffedRatio)
        {
            return StaffingFlag.Understaffed;
        }

        if (rate <= typical * OverstaffedRatio && cell.RateAverageStaff >= MinAverageStaffToCallOverstaffed)
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
        IReadOnlyCollection<MusicDaypart> dayparts,
        IReadOnlyCollection<PartyFact>? parties = null,
        int? laserTagOffsetMinutes = null)
    {
        parties ??= [];

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

        Dictionary<DateTime, List<(DateTime StartUtc, DateTime EndUtc)>> partyWindows = IndexWindows(parties
            .Select(x => LaserWindow(x, laserTagOffsetMinutes))
            .Where(x => x is not null)
            .Select(x => x!.Value));

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

            if (IsInAnyWindow(partyWindows, start))
            {
                slot.PartyGames++;
            }
        }

        // A party is on in every hour it overlaps; on the night the clocks go back both real 01:00
        // hours are one slot, so a party is only counted once in it.
        Dictionary<DateOnly, DayTotals> partyDays = [];
        int partiesWithoutLaserTime = 0;

        foreach (PartyFact party in parties)
        {
            DateTime start = party.StartUtc < rangeStartUtc ? rangeStartUtc : party.StartUtc;
            DateTime end = party.EndUtc > rangeEndUtc ? rangeEndUtc : party.EndUtc;
            HashSet<SlotKey> seen = [];

            foreach ((DateTime hourStart, _) in SplitIntoHours(start, end))
            {
                (DateOnly date, int hour) = LocalHour(hourStart);
                SlotKey key = new(date, hour);

                if (seen.Add(key))
                {
                    SlotTotals slot = totals[key];
                    slot.Parties++;
                    slot.ExpectedChildren += Math.Max(0, party.ExpectedChildren);
                }
            }

            // Days and the totals count a party once, on the day it starts.
            DateOnly startDay = RotaTime.LocalDate(party.StartUtc);

            if (startDay < from || startDay > to)
            {
                continue;
            }

            if (!partyDays.TryGetValue(startDay, out DayTotals? day))
            {
                partyDays[startDay] = day = new DayTotals();
            }

            day.Parties++;
            day.ExpectedChildren += Math.Max(0, party.ExpectedChildren);

            if (IsMissingLaserTime(party, laserTagOffsetMinutes))
            {
                partiesWithoutLaserTime++;
            }
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

            slots.Add(new InsightsSlot(key.Date, key.Hour, isOpen, daypart, t.Games, t.Players, t.Clocked, t.Rota,
                t.PartyGames, t.Parties, t.ExpectedChildren));
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
                StaffingFlag.None,
                g.Sum(x => x.PartyGames),
                g.Count(x => x.HasParty),
                g.Sum(x => x.ExpectedChildren),
                g.Count(x => !x.HasParty),
                g.Where(x => !x.HasParty).Sum(x => x.Games),
                g.Where(x => !x.HasParty).Sum(x => x.ClockedHours)))
            .OrderBy(x => ((int)x.Day + 6) % 7)
            .ThenBy(x => x.Hour)];

        InsightsTotals summaryTotals = new(
            slots.Sum(x => x.Games),
            slots.Sum(x => x.Players),
            slots.Sum(x => x.ClockedHours),
            slots.Sum(x => x.RotaHours),
            slots.Where(x => x.IsOpen).Sum(x => x.ClockedHours),
            slots.Sum(x => x.PartyGames),
            partyDays.Values.Sum(x => x.Parties),
            partyDays.Values.Sum(x => x.ExpectedChildren),
            partiesWithoutLaserTime);

        decimal? median = Median(rawCells.Where(IsTypicalRateSample).Select(x => x.GamesPerStaffHour!.Value));
        int days = to.DayNumber - from.DayNumber + 1;

        // Only games outside party hours say anything about normal trade.
        bool anyPartyHours = slots.Any(x => x.HasParty);
        int flagGames = slots.Where(x => !x.HasParty).Sum(x => x.Games);

        string? reason = days < MinFlagRangeDays
            ? "Pick at least two weeks to see staffing flags."
            : flagGames < MinFlagGames
                ? anyPartyHours
                    ? $"Staffing flags need at least {MinFlagGames} games outside party hours in the range."
                    : $"Staffing flags need at least {MinFlagGames} games in the range."
                : summaryTotals.ClockedHoursWhileOpen <= 0
                    ? "Staffing flags need clocked hours from timesheets."
                    : median is null
                        ? "No hour has enough games and clocked time yet to show what's typical."
                        : null;

        bool flagsAvailable = reason is null;

        // "Mostly parties" is a fact about the bookings, so it shows even before flags are available.
        List<InsightsCell> cells = [.. rawCells.Select(x => x with
        {
            Flag = flagsAvailable ? Flag(x, median)
                : IsMostlyParties(x) ? StaffingFlag.PartyHours
                : StaffingFlag.InsufficientData
        })];

        List<InsightsDay> dayRows = [.. slots
            .GroupBy(x => x.Date)
            .Select(g => new InsightsDay(
                g.Key,
                g.Sum(x => x.Games),
                g.Sum(x => x.Players),
                g.Sum(x => x.ClockedHours),
                g.Sum(x => x.RotaHours),
                g.Count(x => x.IsOpen),
                g.Sum(x => x.PartyGames),
                partyDays.GetValueOrDefault(g.Key)?.Parties ?? 0,
                partyDays.GetValueOrDefault(g.Key)?.ExpectedChildren ?? 0))
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
