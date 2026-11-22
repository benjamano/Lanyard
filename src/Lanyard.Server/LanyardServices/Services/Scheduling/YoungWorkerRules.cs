using System.Globalization;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// Someone under 18 (YoungWorkerStatus.DateOfBirth) and their company's night window, as the rules
// need them. The 18th birthday and school leaving date are worked out from the date of birth.
public sealed record YoungWorkerProfile(DateOnly DateOfBirth, YoungWorkerNightWindow NightWindow)
{
    public bool IsUnder18On(DateOnly day) => YoungWorkerAge.IsUnder18On(DateOfBirth, day);

    public bool IsUnderSchoolAgeOn(DateOnly day) => day < YoungWorkerAge.SchoolLeavingDate(DateOfBirth);
}

// A stretch of work: a shift, reduced to what the young worker rules look at.
public readonly record struct WorkBlock(Guid Id, DateTime StartUtc, DateTime EndUtc, int BreakMinutes)
{
    public static WorkBlock From(Shift shift) => new(shift.Id, shift.StartUtc, shift.EndUtc, shift.BreakMinutes);

    public decimal PaidHours => (decimal)Math.Max(0, (EndUtc - StartUtc).TotalMinutes - BreakMinutes) / 60m;
}

// Which young worker limit a breach is of. Never stored.
public enum YoungWorkerRule
{
    DailyHours = 0,
    WeeklyHours = 1,
    Break = 2,
    DailyRest = 3,
    WeeklyRest = 4,
    NightWork = 5,
    CoreNight = 6,
    UnderSchoolLeavingAge = 7
}

public sealed record YoungWorkerBreach(YoungWorkerRule Rule, string Message);

// The UK limits on young workers (over school leaving age, under 18 - Working Time Regulations
// 1998 as summarised on GOV.UK): 8 hours' work a day and 40 a week with no averaging; a 30 minute
// break when working more than 4.5 hours; 12 hours' rest between working days; 48 hours' rest in a
// row each week; no work in the night window (22:00-06:00, or 23:00-07:00 by contract) and never
// between midnight and 04:00. Anyone still under school leaving age is under child employment
// rules, which Lanyard doesn't check, so they always breach.
//
// One pure class shared by the rota warnings, the shift dialog, manual saves, shift claims, swaps,
// suggestions and Auto-build, so none of them can disagree. Everything is venue-local (RotaTime):
// days are local calendar days, weeks run Monday to Sunday, and night windows are built from local
// clock times so the nights the clocks change are 9 or 7 hours long, as they really are.
public static class YoungWorkerRules
{
    public const decimal MaxDailyHours = 8m;
    public const decimal MaxWeeklyHours = 40m;
    public const decimal BreakNeededOverHours = 4.5m;
    public const int MinimumBreakMinutes = 30;
    public static readonly TimeSpan DailyRest = TimeSpan.FromHours(12);
    public static readonly TimeSpan WeeklyRest = TimeSpan.FromHours(48);
    private static readonly TimeOnly CoreNightStart = new(0, 0);
    private static readonly TimeOnly CoreNightEnd = new(4, 0);

    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");

    // How far either side of a shift the other shifts must be loaded for Evaluate to see everything
    // that matters: the rest of its Monday-Sunday week, plus a day either side for daily rest.
    public static readonly TimeSpan LookAround = TimeSpan.FromDays(8);

    public static (TimeOnly Start, TimeOnly End) NightHours(YoungWorkerNightWindow window) => window switch
    {
        YoungWorkerNightWindow.ElevenToSeven => (new TimeOnly(23, 0), new TimeOnly(7, 0)),
        _ => (new TimeOnly(22, 0), new TimeOnly(6, 0))
    };

    // The limits broken with `candidate` on the rota that it is part of: its own hours, break and
    // night work, and the day, week or rest gap it works in - including one already over the limit,
    // since adding to it makes it worse. A limit broken somewhere the candidate doesn't touch (a long
    // Tuesday, when it's on Thursday) is the rota's warning, not this shift's. `others` are the
    // person's other shifts at every location within LookAround of it; the candidate's own id is
    // ignored among them, so an edit isn't counted twice.
    public static List<YoungWorkerBreach> Evaluate(YoungWorkerProfile profile, WorkBlock candidate, IEnumerable<WorkBlock> others)
    {
        DateOnly day = RotaTime.LocalDate(candidate.StartUtc);

        if (!profile.IsUnder18On(day))
        {
            return [];
        }

        List<WorkBlock> with = [.. others.Where(x => x.Id != candidate.Id && x.EndUtc > x.StartUtc), candidate];

        DateOnly firstDay = RotaTime.GetWeekStart(day).AddDays(-1);
        DateOnly lastDay = RotaTime.GetWeekStart(RotaTime.LocalDate(candidate.EndUtc)).AddDays(7);

        return Breaches(profile, with, firstDay, lastDay)
            .Where(x => x.Involves.Contains(candidate.Id))
            .Select(x => x.Breach)
            .ToList();
    }

    public static List<YoungWorkerBreach> Evaluate(YoungWorkerProfile profile, Shift candidate, IEnumerable<Shift> others) =>
        Evaluate(profile, WorkBlock.From(candidate), others.Where(x => x.IsActive).Select(WorkBlock.From));

    // Everything already wrong in one Monday-starting week, for the rota's warnings. `shifts` must
    // include the week before and after (LookAround) for rest to be judged at the edges.
    public static List<YoungWorkerBreach> EvaluateWeek(YoungWorkerProfile profile, DateOnly weekStart, IEnumerable<WorkBlock> shifts)
    {
        List<WorkBlock> blocks = shifts.Where(x => x.EndUtc > x.StartUtc).ToList();

        return Breaches(profile, blocks, weekStart, weekStart.AddDays(6))
            .Where(x => x.Day >= weekStart && x.Day <= weekStart.AddDays(6))
            .Select(x => x.Breach)
            .DistinctBy(x => x.Message)
            .ToList();
    }

    // The break a shift needs for this person: at least 30 minutes once it's over 4.5 hours of work.
    // Auto-build sets this on the draft rather than skip an under-18 over a short break.
    public static int RequiredBreakMinutes(YoungWorkerProfile profile, WorkBlock shift) =>
        profile.IsUnder18On(RotaTime.LocalDate(shift.StartUtc)) && shift.PaidHours > BreakNeededOverHours && shift.BreakMinutes < MinimumBreakMinutes
            ? MinimumBreakMinutes
            : shift.BreakMinutes;

    // One breach, the day (or week start) it's on, and the shifts that make it up - so a new shift
    // is only blamed for limits it is part of.
    private sealed record Found(DateOnly Day, IReadOnlyCollection<Guid> Involves, YoungWorkerBreach Breach);

    private static List<Found> Breaches(YoungWorkerProfile profile, List<WorkBlock> blocks, DateOnly firstDay, DateOnly lastDay)
    {
        List<Found> found = [];
        (TimeOnly nightStart, TimeOnly nightEnd) = NightHours(profile.NightWindow);

        // Per shift: school age, night work, breaks.
        foreach (WorkBlock block in blocks)
        {
            DateOnly day = RotaTime.LocalDate(block.StartUtc);

            if (day < firstDay || day > lastDay || !profile.IsUnder18On(day))
            {
                continue;
            }

            if (profile.IsUnderSchoolAgeOn(day))
            {
                found.Add(new Found(day, [block.Id], new YoungWorkerBreach(YoungWorkerRule.UnderSchoolLeavingAge,
                    "Under school leaving age: Lanyard doesn't check child employment rules, so only a manager can roster them.")));
            }

            if (Overlaps(block, CoreNightStart, CoreNightEnd))
            {
                found.Add(new Found(day, [block.Id], new YoungWorkerBreach(YoungWorkerRule.CoreNight,
                    "Under 18: can't work between midnight and 04:00.")));
            }
            else if (Overlaps(block, nightStart, nightEnd))
            {
                found.Add(new Found(day, [block.Id], new YoungWorkerBreach(YoungWorkerRule.NightWork,
                    $"Under 18: can't work between {nightStart:HH\\:mm} and {nightEnd:HH\\:mm}.")));
            }

            if (block.PaidHours > BreakNeededOverHours && block.BreakMinutes < MinimumBreakMinutes)
            {
                found.Add(new Found(day, [block.Id], new YoungWorkerBreach(YoungWorkerRule.Break,
                    $"Under 18: a shift over {BreakNeededOverHours:0.#} h needs at least a {MinimumBreakMinutes} minute break.")));
            }
        }

        // Hours per local day, a shift that crosses midnight split at midnight, and who worked them.
        Dictionary<DateOnly, decimal> hoursByDay = [];
        Dictionary<DateOnly, List<Guid>> blocksByDay = [];

        foreach (WorkBlock block in blocks)
        {
            foreach ((DateOnly day, decimal hours) in HoursByDay(block))
            {
                hoursByDay[day] = hoursByDay.GetValueOrDefault(day) + hours;

                if (!blocksByDay.TryGetValue(day, out List<Guid>? ids))
                {
                    blocksByDay[day] = ids = [];
                }

                ids.Add(block.Id);
            }
        }

        foreach ((DateOnly day, decimal hours) in hoursByDay)
        {
            if (day >= firstDay && day <= lastDay && profile.IsUnder18On(day) && hours > MaxDailyHours)
            {
                found.Add(new Found(day, blocksByDay[day], new YoungWorkerBreach(YoungWorkerRule.DailyHours,
                    $"Under 18: {Hours(hours)} on {DayLabel(day)} (the limit is {MaxDailyHours:0} h a day).")));
            }
        }

        // Hours, and 48 hours off in a row, per Monday-Sunday week.
        for (DateOnly week = RotaTime.GetWeekStart(firstDay); week <= lastDay; week = week.AddDays(7))
        {
            if (!profile.IsUnder18On(week) && !profile.IsUnder18On(week.AddDays(6)))
            {
                continue;
            }

            // Only the days they're still under 18 count: in the week of their 18th birthday, the
            // days from it on are an adult's.
            List<DateOnly> youngDays = hoursByDay.Keys.Where(x => x >= week && x < week.AddDays(7) && profile.IsUnder18On(x)).ToList();
            decimal weekHours = youngDays.Sum(x => hoursByDay[x]);

            if (weekHours > MaxWeeklyHours)
            {
                found.Add(new Found(week, youngDays.SelectMany(x => blocksByDay[x]).Distinct().ToList(), new YoungWorkerBreach(YoungWorkerRule.WeeklyHours,
                    $"Under 18: {Hours(weekHours)} in the week of {DayLabel(week)} (the limit is {MaxWeeklyHours:0} h a week).")));
            }

            if (!HasWeeklyRest(blocks, week))
            {
                found.Add(new Found(week, InWeek(blocks, week), new YoungWorkerBreach(YoungWorkerRule.WeeklyRest,
                    $"Under 18: no {WeeklyRest.TotalHours:0} hours off in a row in the week of {DayLabel(week)}.")));
            }
        }

        // 12 hours between the last work of one day and the first of the next. Gaps within a day
        // (a morning shift, then a party in the evening) are breaks, not daily rest.
        // The rest is made by the last shift of one day and the first of the next, so those are the
        // two it involves - not a split shift in the middle of a day.
        List<(DateOnly Day, WorkBlock First, WorkBlock Last)> days = blocks
            .GroupBy(x => RotaTime.LocalDate(x.StartUtc))
            .Select(g => (g.Key, g.MinBy(x => x.StartUtc), g.MaxBy(x => x.EndUtc)))
            .OrderBy(x => x.Key)
            .ToList();

        for (int i = 1; i < days.Count; i++)
        {
            (DateOnly day, WorkBlock first, _) = days[i];
            WorkBlock previousLast = days[i - 1].Last;
            TimeSpan rest = first.StartUtc - previousLast.EndUtc;

            if (day >= firstDay && day <= lastDay && profile.IsUnder18On(day) && rest < DailyRest)
            {
                found.Add(new Found(day, [first.Id, previousLast.Id], new YoungWorkerBreach(YoungWorkerRule.DailyRest,
                    $"Under 18: only {Hours((decimal)Math.Max(0, rest.TotalHours))} off before {DayLabel(day)} (they need {DailyRest.TotalHours:0} h between working days).")));
            }
        }

        return found;
    }

    // The shifts working any part of a Monday-Sunday week.
    private static List<Guid> InWeek(List<WorkBlock> blocks, DateOnly week)
    {
        DateTime weekStartUtc = RotaTime.StartOfDayUtc(week);
        DateTime weekEndUtc = RotaTime.StartOfDayUtc(week.AddDays(7));

        return blocks.Where(x => x.StartUtc < weekEndUtc && x.EndUtc > weekStartUtc).Select(x => x.Id).ToList();
    }

    // Whether, between Monday 00:00 and the next Monday 00:00, there are 48 hours in a row with no
    // work. Only time inside the week counts, so a weekend off at the end of one week doesn't also
    // count for the next. An unbuilt rest of the week is time off, so a rota still being built
    // passes until the week fills up.
    private static bool HasWeeklyRest(List<WorkBlock> blocks, DateOnly week)
    {
        DateTime weekStartUtc = RotaTime.StartOfDayUtc(week);
        DateTime weekEndUtc = RotaTime.StartOfDayUtc(week.AddDays(7));

        List<(DateTime Start, DateTime End)> busy = blocks
            .Where(x => x.StartUtc < weekEndUtc && x.EndUtc > weekStartUtc)
            .Select(x => (Start: x.StartUtc < weekStartUtc ? weekStartUtc : x.StartUtc, End: x.EndUtc > weekEndUtc ? weekEndUtc : x.EndUtc))
            .OrderBy(x => x.Start)
            .ToList();

        DateTime freeFrom = weekStartUtc;

        foreach ((DateTime start, DateTime end) in busy)
        {
            if (start - freeFrom >= WeeklyRest)
            {
                return true;
            }

            if (end > freeFrom)
            {
                freeFrom = end;
            }
        }

        return weekEndUtc - freeFrom >= WeeklyRest;
    }

    // Whether a shift touches the window from `start` to `end` (venue time, crossing midnight when
    // end <= start) on any night. Each night is built from local clock times, then compared in UTC.
    // Ending exactly as the window opens, or starting exactly as it closes, is allowed.
    private static bool Overlaps(WorkBlock block, TimeOnly start, TimeOnly end)
    {
        bool crossesMidnight = end <= start;

        for (DateOnly day = RotaTime.LocalDate(block.StartUtc).AddDays(-1); day <= RotaTime.LocalDate(block.EndUtc); day = day.AddDays(1))
        {
            DateTime windowStart = RotaTime.ToUtc(day, start);
            DateTime windowEnd = RotaTime.ToUtc(crossesMidnight ? day.AddDays(1) : day, end);

            if (block.StartUtc < windowEnd && block.EndUtc > windowStart)
            {
                return true;
            }
        }

        return false;
    }

    // A shift's working hours on each local day it touches, the break taken off in proportion.
    private static IEnumerable<(DateOnly Day, decimal Hours)> HoursByDay(WorkBlock block)
    {
        double totalMinutes = (block.EndUtc - block.StartUtc).TotalMinutes;

        if (totalMinutes <= 0)
        {
            yield break;
        }

        decimal paidRatio = block.PaidHours / (decimal)(totalMinutes / 60);

        for (DateOnly day = RotaTime.LocalDate(block.StartUtc); day <= RotaTime.LocalDate(block.EndUtc); day = day.AddDays(1))
        {
            DateTime dayStart = RotaTime.StartOfDayUtc(day);
            DateTime dayEnd = RotaTime.StartOfDayUtc(day.AddDays(1));
            DateTime from = block.StartUtc > dayStart ? block.StartUtc : dayStart;
            DateTime to = block.EndUtc < dayEnd ? block.EndUtc : dayEnd;

            if (to > from)
            {
                yield return (day, (decimal)(to - from).TotalHours * paidRatio);
            }
        }
    }

    private static string Hours(decimal hours) => $"{Math.Round(hours, 2):0.##} h";

    private static string DayLabel(DateOnly day) => day.ToString("ddd d MMM", Uk);
}
