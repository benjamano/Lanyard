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
// A template position's regulars get its shifts first whenever they can work them (the same hard
// rules apply). Shifts with regulars are filled first; then the hardest - the ones the fewest
// people could work - so a scarce person isn't used up on a shift plenty of others could have done;
// then in time order.
public static class RotaAutoBuildRules
{
    private static readonly SuggestionReasonKind[] AutoBlocking =
        [SuggestionReasonKind.OverMaxHours, SuggestionReasonKind.PendingTimeOff];

    // The shifts the templates want on each day from..to, each slot once per day it applies.
    // Holidays are the location's (RotaHolidayRules): nothing on a closed day, a bank holiday uses
    // the templates of the weekday it's staffed like, and each template's Runs setting picks term
    // time or school holidays.
    public static List<DemandSlot> ExpandTemplates(IEnumerable<ShiftTemplate> templates, DateOnly from, DateOnly to,
        IReadOnlyCollection<RotaHoliday>? holidays = null)
    {
        List<DemandSlot> slots = [];
        List<ShiftTemplate> list = templates.ToList();

        for (DateOnly day = from; day <= to; day = day.AddDays(1))
        {
            RotaHolidayDay calendar = RotaHolidayRules.For(holidays ?? [], day);

            foreach (ShiftTemplate template in list)
            {
                if (!template.AppliesOn(calendar))
                {
                    continue;
                }

                DateTime startUtc = RotaTime.ToUtc(day, template.StartTime);
                DateTime endUtc = RotaTime.ToUtc(template.RunsPastMidnight ? day.AddDays(1) : day, template.EndTime);

                // Positions needing nobody are kept (Needed 0): parties and hires add to them.
                foreach (ShiftTemplateSlot slot in template.Slots.Where(x => x.Headcount >= 0))
                {
                    List<string> regulars = slot.Regulars.OrderBy(x => x.SortOrder).Select(x => x.UserId).ToList();

                    slots.Add(new DemandSlot(template.Name, day, startUtc, endUtc, template.BreakMinutes,
                        slot.StaffPositionId, slot.StaffPosition?.Name ?? "Position", slot.Headcount, template.Id,
                        null, regulars.Count > 0 ? regulars : null));
                }
            }
        }

        return slots.OrderBy(x => x.StartUtc).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The extra people booked parties and private hires need. They work the whole template shift the
    // booking falls in - a 09:00-14:00 shift with a party at 10:00 - and do the party as part of
    // it, so each of the booking's PartyStaffingRules adds its StaffCount of that position to one
    // template shift: the one overlapping the booking that already lists that position (so a hire's
    // extra Manager joins the late Manager shift, not the CSA evening shift), else any overlapping
    // shift; of those, the one covering most of the booking, then the earliest. Two bookings in one
    // shift add up. Shifts are the template ones from ExpandTemplates for the same days; a booking
    // no template shift overlaps is returned in Uncovered, for a warning, and adds nobody.
    public static (List<DemandSlot> Slots, List<PartyBooking> Uncovered) ExpandParties(
        IEnumerable<PartyBooking> parties, IReadOnlyCollection<PartyStaffingRule> rules, IEnumerable<DemandSlot> templateSlots)
    {
        List<DemandSlot> positions = templateSlots.Where(x => x.PartyBookingId is null).ToList();
        List<DemandSlot> shifts = positions.DistinctBy(x => (x.TemplateId, x.StartUtc, x.EndUtc)).ToList();

        List<DemandSlot> slots = [];
        List<PartyBooking> uncovered = [];

        foreach (PartyBooking party in parties.Where(x => x.Status == PartyBookingStatus.Booked))
        {
            List<PartyStaffingRule> partyRules = PartyStaffingRule.For(rules, party).Where(x => x.StaffCount > 0).ToList();

            if (partyRules.Count == 0)
            {
                continue;
            }

            bool Overlaps(DemandSlot shift) => shift.StartUtc < party.EndUtc && shift.EndUtc > party.StartUtc;

            List<DemandSlot> during = shifts.Where(Overlaps).ToList();

            if (during.Count == 0)
            {
                uncovered.Add(party);
                continue;
            }

            foreach (PartyStaffingRule rule in partyRules)
            {
                List<DemandSlot> withPosition = positions.Where(x => x.StaffPositionId == rule.StaffPositionId && Overlaps(x)).ToList();

                DemandSlot shift = (withPosition.Count > 0 ? withPosition : during)
                    .OrderByDescending(x => Overlap(x, party))
                    .ThenBy(x => x.StartUtc)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .First();

                slots.Add(shift with
                {
                    Name = $"{shift.Name} · {PartyName(party)}",
                    StaffPositionId = rule.StaffPositionId,
                    PositionName = rule.StaffPosition?.Name ?? "Position",
                    Needed = rule.StaffCount,
                    PartyBookingId = party.Id,
                    // Joining a line that lists the position: its regulars are the people to ask first
                    // (the late manager line's regular does a hire's extra manager shift).
                    RegularUserIds = withPosition.Count > 0 ? shift.RegularUserIds : null
                });
            }
        }

        return (slots.OrderBy(x => x.StartUtc).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(), uncovered);
    }

    private static TimeSpan Overlap(DemandSlot shift, PartyBooking party) =>
        (shift.EndUtc < party.EndUtc ? shift.EndUtc : party.EndUtc) - (shift.StartUtc > party.StartUtc ? shift.StartUtc : party.StartUtc);

    // How a party is named on the rota. The type, not the child's name: the shift is shown to the
    // staff member and stays on their record after the party.
    public static string PartyName(PartyBooking party) =>
        party.HireKind is PrivateHireKind kind ? $"Private hire: {kind.Label()}"
        : PartyStaffingRule.Normalise(party.PartyType) is string type ? $"Party: {type}" : "Party";

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
            // Shifts with regulars first, so a regular isn't used up on another shift before their own.
            .OrderBy(x => x.Slot.RegularUserIds is { Count: > 0 } ? 0 : 1)
            .ThenBy(x => x.Possible)
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

            bool Allowed(ShiftSuggestion x) => !x.Reasons.Any(r => AutoBlocking.Contains(r.Kind))
                && !ShortOvernightRest(workers.First(w => w.UserId == x.UserId), shift);

            // A regular who can work it gets it, in the order the template lists them; otherwise the
            // usual ranking decides.
            ShiftSuggestion? regular = slot.RegularUserIds?
                .Select(id => ranking.Ranked.FirstOrDefault(x => x.UserId == id))
                .FirstOrDefault(x => x is not null && Allowed(x));

            ShiftSuggestion? best = regular ?? ranking.Ranked.FirstOrDefault(Allowed);

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

            string? reason = regular is not null ? "Regular on this shift" : best.Positives.FirstOrDefault()?.Text;
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
