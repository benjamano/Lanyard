using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// Everything the ranking needs to know about one person, gathered by ShiftSuggestionService.
// Shifts: their active shifts at every location in the company from the day before the shift's
// week to the day after it (the extra days are for the rest-gap check). CantWorkReason is
// ShiftClaimRules.WhyCantWork's answer - null when they can work it. PendingTimeOffName is the
// type of an undecided time-off request covering the shift's day ("holiday"), or null.
public record SuggestionCandidate(
    string UserId,
    string DisplayName,
    Guid? PrimaryPositionId,
    string? PrimaryPositionName,
    ResolvedContract Contract,
    IReadOnlyCollection<Shift> Shifts,
    string? PendingTimeOffName = null,
    bool HasPendingClaim = false,
    string? CantWorkReason = null);

// Ranks people for one shift. Pure and deterministic - no database, no clock - so it can be
// tested directly and reused by any later "fill the week" drafting.
//
// The order is a fixed list of tie-breaks rather than a weighted score, so it can always be
// explained ("ahead because they need another shift this week"):
//   1. wouldn't go over their weekly maximum
//   2. hasn't asked for that day off
//   3. gets a proper rest either side (11 h, the Working Time Regulations daily rest)
//   4. needs the shift to meet their contract minimum this week
//   5. asked for this shift themselves
//   6. usually works this position
//   7. fewest hours this week for their contract (hours / max, or min, or the team's usual)
//   8. name, then id, so the same inputs always give the same order
// Going over a maximum, a pending day off or a short rest only push someone down: the manager can
// still pick them, as SaveShiftAsync only warns about each.
public static class ShiftSuggestionRules
{
    public static readonly TimeSpan MinimumRest = TimeSpan.FromHours(11);

    // Used for the hours comparison when nobody in the list has a contract maximum or minimum.
    public const decimal DefaultWeeklyHours = 40m;

    private sealed record Scored(
        SuggestionCandidate Person,
        decimal HoursBefore,
        decimal HoursAfter,
        List<SuggestionReason> Reasons,
        bool BreaksMax,
        bool PendingTimeOff,
        bool ShortRest,
        bool HelpsContract,
        bool UsualPosition);

    public static ShiftSuggestions Rank(Shift shift, IReadOnlyCollection<SuggestionCandidate> people)
    {
        DateOnly weekStart = RotaTime.GetWeekStart(RotaTime.LocalDate(shift.StartUtc));
        DateTime weekStartUtc = RotaTime.StartOfDayUtc(weekStart);
        DateTime weekEndUtc = RotaTime.StartOfDayUtc(weekStart.AddDays(7));

        List<ShiftSuggestion> notSuggested = [];
        List<Scored> scored = [];

        foreach (SuggestionCandidate person in people)
        {
            // An edited open shift is in nobody's list, but a caller may still pass it in; never
            // count the shift against itself.
            List<Shift> others = person.Shifts
                .Where(x => x.IsActive && (shift.Id == Guid.Empty || x.Id != shift.Id))
                .ToList();

            List<Shift> week = others.Where(x => x.StartUtc >= weekStartUtc && x.StartUtc < weekEndUtc).ToList();
            decimal before = week.Sum(x => x.PaidHours);
            decimal after = before + shift.PaidHours;

            if (person.CantWorkReason is string reason)
            {
                notSuggested.Add(new ShiftSuggestion(person.UserId, person.DisplayName, person.PrimaryPositionName, 0,
                    before, before, person.Contract.MaxHoursPerWeek.Value, [], reason));
                continue;
            }

            scored.Add(Score(person, shift, weekStart, others, week, before, after));
        }

        decimal teamHours = TeamWeeklyHours(scored.Select(x => x.Person.Contract));

        List<ShiftSuggestion> ranked = scored
            .OrderBy(x => x.BreaksMax)
            .ThenBy(x => x.PendingTimeOff)
            .ThenBy(x => x.ShortRest)
            .ThenByDescending(x => x.HelpsContract)
            .ThenByDescending(x => x.Person.HasPendingClaim)
            .ThenByDescending(x => x.UsualPosition)
            .ThenBy(x => x.HoursBefore / WeeklyHoursFor(x.Person.Contract, teamHours))
            .ThenBy(x => x.Person.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Person.UserId, StringComparer.Ordinal)
            .Select((x, i) => new ShiftSuggestion(x.Person.UserId, x.Person.DisplayName, x.Person.PrimaryPositionName, i + 1,
                x.HoursBefore, x.HoursAfter, x.Person.Contract.MaxHoursPerWeek.Value, x.Reasons, null))
            .ToList();

        notSuggested = notSuggested
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.UserId, StringComparer.Ordinal)
            .ToList();

        return new ShiftSuggestions(ranked, notSuggested);
    }

    private static Scored Score(SuggestionCandidate person, Shift shift, DateOnly weekStart, List<Shift> others, List<Shift> week, decimal before, decimal after)
    {
        ResolvedContract contract = person.Contract;
        List<SuggestionReason> reasons = [];

        List<ContractWarning> warningsBefore = ContractCompliance.EvaluateWeek(person.UserId, weekStart, contract, week);
        List<ContractWarning> warningsAfter = ContractCompliance.EvaluateWeek(person.UserId, weekStart, contract, [.. week, shift]);

        bool helpsContract = false;

        if (warningsBefore.Any(x => x.Kind == ContractWarningKind.BelowMinShifts) && contract.MinShiftsPerWeek.Value is int minShifts)
        {
            decimal minLength = contract.MinShiftLengthHours.Value ?? 0m;

            // A shift too short to count towards the minimum doesn't help with it.
            if (shift.PaidHours > 0 && shift.PaidHours >= minLength)
            {
                int qualifying = week.Count(x => x.PaidHours > 0 && x.PaidHours >= minLength);
                int needed = minShifts - qualifying;

                reasons.Add(new SuggestionReason(SuggestionReasonKind.NeedsShifts,
                    $"Needs {needed} more {(needed == 1 ? "shift" : "shifts")} this week"));
                helpsContract = true;
            }
        }

        if (warningsBefore.Any(x => x.Kind == ContractWarningKind.BelowMinHours) && contract.MinHoursPerWeek.Value is decimal minHours && shift.PaidHours > 0)
        {
            reasons.Add(new SuggestionReason(SuggestionReasonKind.BelowMinHours,
                $"Below their {RotaFormat.Hours(minHours)} weekly minimum"));
            helpsContract = true;
        }

        if (person.HasPendingClaim)
        {
            reasons.Add(new SuggestionReason(SuggestionReasonKind.AskedForShift, "Asked for this shift"));
        }

        bool usualPosition = shift.StaffPositionId is Guid positionId && positionId == person.PrimaryPositionId;

        if (usualPosition)
        {
            reasons.Add(new SuggestionReason(SuggestionReasonKind.UsualPosition, $"Usually works as {person.PrimaryPositionName ?? "this position"}"));
        }

        bool breaksMax = warningsAfter.Any(x => x.Kind == ContractWarningKind.AboveMaxHours);

        if (breaksMax && contract.MaxHoursPerWeek.Value is decimal maxHours)
        {
            reasons.Add(new SuggestionReason(SuggestionReasonKind.OverMaxHours,
                $"Would go over their {RotaFormat.Hours(maxHours)} weekly maximum"));
        }

        bool pendingTimeOff = person.PendingTimeOffName is not null;

        if (person.PendingTimeOffName is string timeOffName)
        {
            reasons.Add(new SuggestionReason(SuggestionReasonKind.PendingTimeOff,
                $"Has asked for {timeOffName.ToLower()} that day (not decided yet)"));
        }

        bool shortRest = false;

        // Shifts that overlap this one never get here (WhyCantWork rules them out), so every other
        // shift is wholly before or wholly after it.
        Shift? previous = others.Where(x => x.EndUtc <= shift.StartUtc).MaxBy(x => x.EndUtc);
        Shift? next = others.Where(x => x.StartUtc >= shift.EndUtc).MinBy(x => x.StartUtc);

        if (previous is not null && shift.StartUtc - previous.EndUtc < MinimumRest)
        {
            reasons.Add(new SuggestionReason(SuggestionReasonKind.ShortRest,
                $"Only {RestHours(shift.StartUtc - previous.EndUtc)} after their previous shift"));
            shortRest = true;
        }

        if (next is not null && next.StartUtc - shift.EndUtc < MinimumRest)
        {
            reasons.Add(new SuggestionReason(SuggestionReasonKind.ShortRest,
                $"Only {RestHours(next.StartUtc - shift.EndUtc)} before their next shift"));
            shortRest = true;
        }

        return new Scored(person, before, after, reasons, breaksMax, pendingTimeOff, shortRest, helpsContract, usualPosition);
    }

    // The weekly hours someone's load is measured against: their maximum, else their minimum, else
    // what's usual for the people being compared.
    public static decimal WeeklyHoursFor(ResolvedContract contract, decimal teamHours)
    {
        decimal? own = contract.MaxHoursPerWeek.Value ?? contract.MinHoursPerWeek.Value;

        return own is decimal hours && hours > 0 ? hours : teamHours;
    }

    // The median of everyone's own weekly hours, so someone with no contract is compared like a
    // typical member of the team; DefaultWeeklyHours when nobody has one.
    public static decimal TeamWeeklyHours(IEnumerable<ResolvedContract> contracts)
    {
        List<decimal> hours = contracts
            .Select(x => x.MaxHoursPerWeek.Value ?? x.MinHoursPerWeek.Value)
            .OfType<decimal>()
            .Where(x => x > 0)
            .Order()
            .ToList();

        if (hours.Count == 0)
        {
            return DefaultWeeklyHours;
        }

        int middle = hours.Count / 2;

        return hours.Count % 2 == 1 ? hours[middle] : (hours[middle - 1] + hours[middle]) / 2m;
    }

    private static string RestHours(TimeSpan gap) => RotaFormat.Hours(Math.Round((decimal)gap.TotalHours, 1));
}
