using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// One person Auto-build could put on shifts. Shifts is their active shifts at every location in the
// company across the weeks being built (padded a day each side), and Auto-build adds each shift it
// gives them to it as it goes, so hours, rest and clashes stay right for the next pick.
// PendingTimeOff maps a day to the type of an undecided time-off request covering it.
public sealed record AutoBuildWorker(
    string UserId,
    string DisplayName,
    Guid? PrimaryPositionId,
    string? PrimaryPositionName,
    ResolvedContract Contract,
    List<Shift> Shifts,
    IReadOnlyCollection<StaffAvailability> Availability,
    IReadOnlyDictionary<DateOnly, string> PendingTimeOff,
    StaffWorkPreference? Wish = null);

// A shift Auto-build decided on: the Shift row to save (UserId null = open) and how to describe it.
public sealed record AutoBuildPick(Shift Shift, AutoBuildLine Line);

// Turns a location's shift templates into the shifts a date range needs, and chooses who works
// them. Pure and deterministic - no database, no clock - so it can be tested directly.
//
// Auto-build only ever adds draft shifts. It never moves or removes a shift already on the rota:
// a shift there with the same position, start and end as a template shift counts towards it (so
// running it twice adds nothing, and shifts a manager made by hand are kept). Who works each new
// shift is ShiftSuggestionRules' first choice, with three extra rules because nobody is checking
// each pick: someone is never put on a shift that would take them over their contract maximum,
// leave them less than 11 hours' rest overnight between one day's work and the next, or fall on a
// day they've asked for off. Two shifts on the same day (a morning shift and a party in the
// afternoon) are a split shift, not a short rest, so they're allowed. If that leaves nobody, the shift is
// made as an open shift for the manager (or a pick-up) to fill.
//
// Shifts are filled hardest first - the ones the fewest people could work - so a scarce person
// isn't used up on a shift plenty of others could have done; then in time order.
public static class RotaAutoBuildRules
{
    private static readonly SuggestionReasonKind[] AutoBlocking =
        [SuggestionReasonKind.OverMaxHours, SuggestionReasonKind.PendingTimeOff];

    // The shifts the templates want on each day from..to, each slot once per day it applies.
    public static List<DemandSlot> ExpandTemplates(IEnumerable<ShiftTemplate> templates, DateOnly from, DateOnly to)
    {
        List<DemandSlot> slots = [];

        foreach (ShiftTemplate template in templates)
        {
            for (DateOnly day = from; day <= to; day = day.AddDays(1))
            {
                if (!template.AppliesOn(day))
                {
                    continue;
                }

                DateTime startUtc = RotaTime.ToUtc(day, template.StartTime);
                DateTime endUtc = RotaTime.ToUtc(template.RunsPastMidnight ? day.AddDays(1) : day, template.EndTime);

                foreach (ShiftTemplateSlot slot in template.Slots.Where(x => x.Headcount > 0))
                {
                    slots.Add(new DemandSlot(template.Name, day, startUtc, endUtc, template.BreakMinutes,
                        slot.StaffPositionId, slot.StaffPosition?.Name ?? "Position", slot.Headcount, template.Id));
                }
            }
        }

        return slots.OrderBy(x => x.StartUtc).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The extra shifts booked parties need: for each party, its type's PartyStaffingRules (or the
    // default rules), each covering the party plus the rule's time before and after.
    public static List<DemandSlot> ExpandParties(IEnumerable<PartyBooking> parties, IReadOnlyCollection<PartyStaffingRule> rules)
    {
        List<DemandSlot> slots = [];

        foreach (PartyBooking party in parties.Where(x => x.Status == PartyBookingStatus.Booked))
        {
            string name = PartyName(party);

            foreach (PartyStaffingRule rule in PartyStaffingRule.For(rules, party.PartyType).Where(x => x.StaffCount > 0))
            {
                DateTime startUtc = party.StartUtc.AddMinutes(-rule.MinutesBefore);
                DateTime endUtc = party.EndUtc.AddMinutes(rule.MinutesAfter);

                slots.Add(new DemandSlot(name, RotaTime.LocalDate(startUtc), startUtc, endUtc, 0,
                    rule.StaffPositionId, rule.StaffPosition?.Name ?? "Position", rule.StaffCount, null, party.Id));
            }
        }

        return slots.OrderBy(x => x.StartUtc).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // What a party's staff shift is called on the rota. The type, not the child's name: the shift
    // is shown to the staff member and stays on their record after the party.
    public static string PartyName(PartyBooking party) =>
        PartyStaffingRule.Normalise(party.PartyType) is string type ? $"Party: {type}" : "Party";

    // Takes off what the rota already has: each active shift at the location with the same
    // position, start and end as a slot (assigned or open, draft or published) counts once towards
    // it - and, for a party's slot, made for that same party. Returns the slots still wanting
    // shifts and how many were already covered.
    public static (List<DemandSlot> Remaining, int Covered) SubtractCovered(IEnumerable<DemandSlot> slots, IEnumerable<Shift> existing)
    {
        (List<DemandSlot> remaining, int covered, _) = Match(slots, existing);
        return (remaining, covered);
    }

    // As SubtractCovered, also returning the existing shifts that didn't count towards anything.
    public static (List<DemandSlot> Remaining, int Covered, List<Shift> Unmatched) Match(IEnumerable<DemandSlot> slots, IEnumerable<Shift> existing)
    {
        List<Shift> unused = existing.Where(x => x.IsActive).ToList();
        List<DemandSlot> remaining = [];
        int covered = 0;

        foreach (DemandSlot slot in slots)
        {
            int needed = slot.Needed;

            while (needed > 0)
            {
                Shift? match = unused.FirstOrDefault(x => x.StaffPositionId == slot.StaffPositionId
                    && x.PartyBookingId == slot.PartyBookingId
                    && x.StartUtc == slot.StartUtc && x.EndUtc == slot.EndUtc);

                if (match is null)
                {
                    break;
                }

                unused.Remove(match);
                needed--;
                covered++;
            }

            if (needed > 0)
            {
                remaining.Add(slot with { Needed = needed });
            }
        }

        return (remaining, covered, unused);
    }

    // Decides who works every shift the slots want. cantWork answers the fixed questions about a
    // person and a shift (not at the location, doesn't hold the position, missing training, booked
    // off, a clash with a shift already on the rota) - ShiftClaimRules.WhyCantWork in practice;
    // clashes with shifts picked earlier in this same run are checked here. Each pick is added to
    // that worker's Shifts.
    public static List<AutoBuildPick> Assign(
        int locationId,
        IEnumerable<DemandSlot> slots,
        IReadOnlyCollection<AutoBuildWorker> workers,
        Func<AutoBuildWorker, Shift, string?> cantWork,
        string actingUserId,
        DateTime nowUtc,
        IReadOnlyDictionary<Guid, StaffPosition>? positions = null)
    {
        // One unit per shift to make, each with how many people could work it at all.
        var units = slots
            .SelectMany(slot => Enumerable.Range(0, slot.Needed).Select(_ => slot))
            .Select((slot, index) =>
            {
                Shift probe = NewShift(locationId, slot, actingUserId, nowUtc, positions);
                int possible = workers.Count(w => cantWork(w, probe) is null && !Unavailable(w, probe));

                return (Slot: slot, Index: index, Possible: possible);
            })
            .OrderBy(x => x.Possible)
            .ThenBy(x => x.Slot.StartUtc)
            .ThenBy(x => x.Index)
            .ToList();

        List<AutoBuildPick> picks = [];

        foreach ((DemandSlot slot, int _, int possible) in units)
        {
            Shift shift = NewShift(locationId, slot, actingUserId, nowUtc, positions);

            List<SuggestionCandidate> candidates = workers.Select(worker => new SuggestionCandidate(
                worker.UserId,
                worker.DisplayName,
                worker.PrimaryPositionId,
                worker.PrimaryPositionName,
                worker.Contract,
                worker.Shifts,
                worker.PendingTimeOff.GetValueOrDefault(slot.Day),
                false,
                cantWork(worker, shift) ?? ClashWithPicked(worker, shift),
                worker.Availability,
                worker.Wish)).ToList();

            ShiftSuggestions ranking = ShiftSuggestionRules.Rank(shift, candidates);
            ShiftSuggestion? best = ranking.Ranked.FirstOrDefault(x => !x.Reasons.Any(r => AutoBlocking.Contains(r.Kind))
                && !ShortOvernightRest(workers.First(w => w.UserId == x.UserId), shift));

            if (best is null)
            {
                string whyOpen = ranking.Ranked.Count > 0
                    ? "Everyone free would go over their weekly maximum, miss their rest between shifts, or has asked for that day off."
                    : possible == 0
                        ? $"Nobody here works as {slot.PositionName} and is free then."
                        : $"Everyone else who could work as {slot.PositionName} then is already on another shift.";

                picks.Add(new AutoBuildPick(shift, Line(slot, null, null, null, whyOpen)));
                continue;
            }

            AutoBuildWorker chosen = workers.First(x => x.UserId == best.UserId);
            shift.UserId = chosen.UserId;
            chosen.Shifts.Add(shift);

            string? reason = best.Positives.FirstOrDefault()?.Text;
            picks.Add(new AutoBuildPick(shift, Line(slot, chosen.UserId, chosen.DisplayName, reason, null)));
        }

        return picks.OrderBy(x => x.Shift.StartUtc).ThenBy(x => x.Line.Name).ThenBy(x => x.Line.PositionName).ThenBy(x => x.Line.DisplayName).ToList();
    }

    private static Shift NewShift(int locationId, DemandSlot slot, string actingUserId, DateTime nowUtc, IReadOnlyDictionary<Guid, StaffPosition>? positions) => new()
    {
        Id = Guid.NewGuid(),
        LocationId = locationId,
        StaffPositionId = slot.StaffPositionId,
        StaffPosition = positions?.GetValueOrDefault(slot.StaffPositionId),
        StartUtc = slot.StartUtc,
        EndUtc = slot.EndUtc,
        BreakMinutes = slot.BreakMinutes,
        Notes = slot.Name,
        PartyBookingId = slot.PartyBookingId,
        IsActive = true,
        CreateDate = nowUtc,
        CreateByUserId = actingUserId
    };

    // Less than the 11-hour rest between a shift on one day and a shift on another.
    public static bool ShortOvernightRest(AutoBuildWorker worker, Shift shift)
    {
        DateOnly day = RotaTime.LocalDate(shift.StartUtc);

        return worker.Shifts.Any(x => x.IsActive && x.Id != shift.Id && RotaTime.LocalDate(x.StartUtc) != day
            && ((x.EndUtc <= shift.StartUtc && shift.StartUtc - x.EndUtc < ShiftSuggestionRules.MinimumRest)
                || (x.StartUtc >= shift.EndUtc && x.StartUtc - shift.EndUtc < ShiftSuggestionRules.MinimumRest)));
    }

    private static string? ClashWithPicked(AutoBuildWorker worker, Shift shift) =>
        worker.Shifts.Any(x => x.IsActive && x.Id != shift.Id && x.StartUtc < shift.EndUtc && x.EndUtc > shift.StartUtc)
            ? "They're already working then."
            : null;

    private static bool Unavailable(AutoBuildWorker worker, Shift shift) =>
        worker.Availability.Count > 0 && StaffAvailabilityRules.Conflict(worker.Availability, shift.StartUtc, shift.EndUtc) is not null;

    private static AutoBuildLine Line(DemandSlot slot, string? userId, string? name, string? reason, string? whyOpen) =>
        new(slot.Name, slot.Day, slot.StartUtc, slot.EndUtc, slot.PositionName, userId, name, reason, whyOpen);
}
