using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// Works out shift templates from a rota that was built by hand, so Auto-build can make the same
// rota. Pure and deterministic - no database, no clock.
//
//  - Shifts are grouped by weekday and position, then by start and end: a shift starting and
//    ending within 15 minutes of a group's first shift joins it, so 09:00 one week and 09:10 the
//    next are the same shift.
//  - A group that ran on at least half the days that weekday was worked becomes a template line.
//    Its headcount is the median number worked across all those days (a day it didn't run counts
//    as 0); its times and break are the most common exact ones.
//  - A group that ran on at least two days, three quarters of them with a private hire going on,
//    and on fewer than half the days without one, is a hire extra: a line with headcount 0, and the
//    hire's staffing (LearnedHireStaffing) adds the people. This is checked first.
//  - Everything else (one-off cover, events) is left out.
//  - Regulars are the people who worked a line on at least half the days it ran, most first, up to
//    its headcount (one for a hire-only line).
public static class TemplateLearningRules
{
    public const int NearMinutes = 15;
    public const double LineShare = 0.5;
    public const double HireShare = 0.75;
    public const double RegularShare = 0.5;

    private sealed record Worked(Shift Shift, DateOnly Day, TimeOnly Start, TimeOnly End);

    // Whether two times of day are within NearMinutes of each other (across midnight too).
    public static bool Near(TimeOnly a, TimeOnly b)
    {
        int diff = Math.Abs((a.Hour * 60 + a.Minute) - (b.Hour * 60 + b.Minute));
        return Math.Min(diff, 24 * 60 - diff) <= NearMinutes;
    }

    // Groups shifts of one weekday and position whose starts and ends are near each other.
    private static List<List<Worked>> Cluster(IEnumerable<Worked> shifts)
    {
        List<List<Worked>> clusters = [];

        foreach (Worked shift in shifts.OrderBy(x => x.Start).ThenBy(x => x.End).ThenBy(x => x.Day))
        {
            List<Worked>? cluster = clusters.FirstOrDefault(c => Near(c[0].Start, shift.Start) && Near(c[0].End, shift.End));

            if (cluster is null)
            {
                clusters.Add([shift]);
            }
            else
            {
                cluster.Add(shift);
            }
        }

        return clusters;
    }

    public static TemplateLearningResult Learn(
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<Shift> shifts,
        IReadOnlyCollection<PartyBooking> bookings,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<Guid, string> positionNames,
        IReadOnlyCollection<ShiftTemplate> existing,
        IReadOnlyCollection<PartyStaffingRule> existingRules)
    {
        List<Worked> worked = shifts
            .Where(x => x.IsActive && x.StaffPositionId is not null)
            .Select(x =>
            {
                TimeOnly start = RotaTime.LocalTime(x.StartUtc);
                TimeOnly end = RotaTime.LocalTime(x.EndUtc);
                return new Worked(x, RotaTime.LocalDate(x.StartUtc), start, end);
            })
            .Where(x => x.Day >= from && x.Day <= to)
            .ToList();

        List<PartyBooking> hires = bookings.Where(x => x.HireKind is not null && x.Status == PartyBookingStatus.Booked).ToList();

        // The days each weekday was worked at all.
        Dictionary<DayOfWeek, List<DateOnly>> openDays = worked
            .Select(x => x.Day)
            .Distinct()
            .GroupBy(x => x.DayOfWeek)
            .ToDictionary(g => g.Key, g => g.Order().ToList());

        List<(DayOfWeek Day, TimeOnly Start, TimeOnly End, int Break, LearnedLine Line, List<Worked> Shifts)> lines = [];
        int leftOut = 0;

        IEnumerable<(DayOfWeek DayOfWeek, Guid Position, List<Worked> Shifts)> groups = worked
            .GroupBy(x => (x.Day.DayOfWeek, Position: x.Shift.StaffPositionId!.Value))
            .SelectMany(g => Cluster(g).Select(c => (g.Key.DayOfWeek, g.Key.Position, c)));

        foreach ((DayOfWeek dayOfWeek, Guid position, List<Worked> group) in groups)
        {
            List<DateOnly> open = openDays[dayOfWeek];
            TimeOnly typicalStart = MostCommon(group.Select(x => x.Start));
            TimeOnly typicalEnd = MostCommon(group.Select(x => x.End));
            ILookup<DateOnly, Worked> byDay = group.ToLookup(x => x.Day);
            List<DateOnly> ran = byDay.Select(x => x.Key).Order().ToList();

            // A hire extra: it mostly ran while a hire was on, and mostly didn't on days without one.
            // Checked first, so a shift that only ran because there was a hire most weeks isn't
            // learned as an every-week shift.
            HashSet<DateOnly> hireDays = open.Where(day => hires.Any(h => Overlaps(day, typicalStart, typicalEnd, h))).ToHashSet();
            int duringHire = ran.Count(day => byDay[day].Any(w => hires.Any(h => Overlaps(w.Shift, h))));
            int quietDays = open.Count - hireDays.Count;
            int ranOnQuietDays = ran.Count(day => !hireDays.Contains(day));

            bool hireOnly = ran.Count >= 2 && duringHire >= ran.Count * HireShare
                && quietDays > 0 && ranOnQuietDays < quietDays * LineShare;

            int headcount = Median(open.Select(d => byDay[d].Count()));
            bool isLine = !hireOnly && ran.Count >= open.Count * LineShare && headcount > 0;

            if (!isLine && !hireOnly)
            {
                leftOut += group.Count();
                continue;
            }

            int wanted = hireOnly ? 1 : headcount;

            List<LearnedRegular> regulars = group
                .Where(x => x.Shift.UserId is not null)
                .GroupBy(x => x.Shift.UserId!)
                .Select(g => new LearnedRegular(g.Key, names.GetValueOrDefault(g.Key, "Someone"), g.Select(x => x.Day).Distinct().Count()))
                .Where(x => x.Days >= ran.Count * RegularShare)
                .OrderByDescending(x => x.Days)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(wanted)
                .ToList();

            int breakMinutes = MostCommon(group.Select(x => x.Shift.BreakMinutes));

            lines.Add((dayOfWeek, typicalStart, typicalEnd, breakMinutes,
                new LearnedLine(position, positionNames.GetValueOrDefault(position, "Position"),
                    hireOnly ? 0 : headcount, hireOnly, regulars, ran.Count, open.Count),
                group));
        }

        List<LearnedTemplate> templates = lines
            .GroupBy(x => (x.Day, x.Start, x.End))
            .Select(g =>
            {
                List<LearnedLine> templateLines = g.Select(x => x.Line).OrderBy(x => x.PositionName, StringComparer.OrdinalIgnoreCase).ToList();
                int breakMinutes = MostCommon(g.Select(x => x.Break));
                string name = $"{RotaFormat.Uk.DateTimeFormat.GetDayName(g.Key.Day)} {g.Key.Start:HH\\:mm}–{g.Key.End:HH\\:mm}";

                return Compare(new LearnedTemplate(name, g.Key.Day, g.Key.Start, g.Key.End, breakMinutes, templateLines,
                    LearnedStatus.New, null, []), existing, positionNames, names);
            })
            .OrderBy(x => ((int)x.DayOfWeek + 6) % 7)
            .ThenBy(x => x.StartTime)
            .ThenBy(x => x.EndTime)
            .ToList();

        List<LearnedHireStaffing> staffing = LearnHireStaffing(lines.Where(x => x.Line.HireOnly).SelectMany(x => x.Shifts).ToList(), hires, positionNames, existingRules);

        return new TemplateLearningResult(from, to, worked.Count, leftOut, templates, staffing, null);
    }

    // For each kind of hire: the median number of hire-only shifts of each position worked during
    // a hire of that kind.
    private static List<LearnedHireStaffing> LearnHireStaffing(
        List<Worked> hireShifts, List<PartyBooking> hires, IReadOnlyDictionary<Guid, string> positionNames, IReadOnlyCollection<PartyStaffingRule> existingRules)
    {
        List<LearnedHireStaffing> result = [];

        foreach (IGrouping<PrivateHireKind, PartyBooking> kind in hires.GroupBy(x => x.HireKind!.Value))
        {
            List<Guid> positions = hireShifts.Where(w => kind.Any(h => Overlaps(w.Shift, h))).Select(w => w.Shift.StaffPositionId!.Value).Distinct().ToList();

            foreach (Guid position in positions)
            {
                int count = Median(kind.Select(h => hireShifts.Count(w => w.Shift.StaffPositionId == position && Overlaps(w.Shift, h))));

                if (count <= 0)
                {
                    continue;
                }

                bool alreadySet = existingRules.Any(r => r.IsActive && r.HireKind == kind.Key && r.StaffPositionId == position && r.StaffCount == count);

                result.Add(new LearnedHireStaffing(kind.Key, position, positionNames.GetValueOrDefault(position, "Position"), count, kind.Count(), alreadySet));
            }
        }

        return result.OrderBy(x => x.HireKind).ThenBy(x => x.PositionName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // New, Same or Changed against the location's active templates on that day at those times.
    private static LearnedTemplate Compare(LearnedTemplate learned, IReadOnlyCollection<ShiftTemplate> existing,
        IReadOnlyDictionary<Guid, string> positionNames, IReadOnlyDictionary<string, string> names)
    {
        // Within 15 minutes, like the learning: a 09:00 template is the 09:05 shift, not a new one.
        ShiftTemplate? match = existing
            .Where(x => x.IsActive && x.DayOfWeek == learned.DayOfWeek && Near(x.StartTime, learned.StartTime) && Near(x.EndTime, learned.EndTime))
            .OrderBy(x => Math.Abs((x.StartTime - learned.StartTime).TotalMinutes) + Math.Abs((x.EndTime - learned.EndTime).TotalMinutes))
            .FirstOrDefault();

        if (match is null)
        {
            return learned;
        }

        List<string> changes = [];

        foreach (LearnedLine line in learned.Lines)
        {
            ShiftTemplateSlot? slot = match.Slots.FirstOrDefault(x => x.StaffPositionId == line.StaffPositionId);

            if (slot is null)
            {
                changes.Add($"Add {line.Headcount} × {line.PositionName}");
                continue;
            }

            if (slot.Headcount != line.Headcount)
            {
                changes.Add($"{line.PositionName}: {slot.Headcount} → {line.Headcount}");
            }

            List<string> had = slot.Regulars.OrderBy(x => x.SortOrder).Select(x => x.UserId).ToList();
            List<string> learnedRegulars = line.Regulars.Select(x => x.UserId).ToList();

            if (!had.SequenceEqual(learnedRegulars))
            {
                string who = learnedRegulars.Count == 0 ? "none" : string.Join(", ", line.Regulars.Select(x => x.Name));
                changes.Add($"{line.PositionName} regulars: {who}");
            }
        }

        foreach (ShiftTemplateSlot slot in match.Slots.Where(s => learned.Lines.All(l => l.StaffPositionId != s.StaffPositionId)))
        {
            changes.Add($"Remove {slot.Headcount} × {positionNames.GetValueOrDefault(slot.StaffPositionId, slot.StaffPosition?.Name ?? "Position")}");
        }

        if (match.BreakMinutes != learned.BreakMinutes)
        {
            changes.Add($"Break: {match.BreakMinutes} → {learned.BreakMinutes} min");
        }

        if (match.StartTime != learned.StartTime || match.EndTime != learned.EndTime)
        {
            changes.Add($"Times: {match.StartTime:HH\\:mm}–{match.EndTime:HH\\:mm} → {learned.StartTime:HH\\:mm}–{learned.EndTime:HH\\:mm}");
        }

        return learned with
        {
            Name = match.Name,
            Status = changes.Count == 0 ? LearnedStatus.Same : LearnedStatus.Changed,
            ExistingTemplateId = match.Id,
            Changes = changes
        };
    }

    // The learned template as a ShiftTemplate to save (Id set when it updates an existing one).
    public static ShiftTemplate ToTemplate(LearnedTemplate learned, int locationId) => new()
    {
        Id = learned.ExistingTemplateId ?? Guid.Empty,
        CompanyId = 0,
        LocationId = locationId,
        Name = learned.Name,
        DayOfWeek = learned.DayOfWeek,
        StartTime = learned.StartTime,
        EndTime = learned.EndTime,
        BreakMinutes = learned.BreakMinutes,
        Slots = learned.Lines.Select(l => new ShiftTemplateSlot
        {
            ShiftTemplateId = Guid.Empty,
            StaffPositionId = l.StaffPositionId,
            Headcount = l.Headcount,
            Regulars = l.Regulars.Select((r, i) => new ShiftTemplateSlotRegular { ShiftTemplateSlotId = Guid.Empty, UserId = r.UserId, SortOrder = i }).ToList()
        }).ToList()
    };

    private static bool Overlaps(Shift shift, PartyBooking booking) => shift.StartUtc < booking.EndUtc && shift.EndUtc > booking.StartUtc;

    // Whether a hire overlaps the shift's times on that day.
    private static bool Overlaps(DateOnly day, TimeOnly start, TimeOnly end, PartyBooking booking)
    {
        DateTime startUtc = RotaTime.ToUtc(day, start);
        DateTime endUtc = RotaTime.ToUtc(end <= start ? day.AddDays(1) : day, end);
        return startUtc < booking.EndUtc && endUtc > booking.StartUtc;
    }

    private static int Median(IEnumerable<int> values)
    {
        // The upper median, so a shift worked on exactly half the days counts.
        List<int> sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private static T MostCommon<T>(IEnumerable<T> values) where T : IComparable<T> =>
        values.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
}
