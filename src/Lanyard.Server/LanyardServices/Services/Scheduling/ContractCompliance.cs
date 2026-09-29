using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// Pure evaluation of one person's week against their resolved contract, kept free of the database
// so the rules can be tested directly and reused by the future automated scheduler.
public static class ContractCompliance
{
    // shiftsInWeek: the person's active shifts that start in this Monday-starting week, at any
    // location in the company. Draft shifts count too - the warning is for the manager building
    // the rota, before anything is published.
    public static List<ContractWarning> EvaluateWeek(string userId, DateOnly weekStart, ResolvedContract contract, IReadOnlyCollection<Shift> shiftsInWeek)
    {
        List<ContractWarning> warnings = [];

        if (!contract.HasAnyRequirement)
        {
            return warnings;
        }

        decimal totalHours = shiftsInWeek.Sum(x => x.PaidHours);

        if (contract.MinShiftsPerWeek.Value is int minShifts && minShifts > 0)
        {
            decimal minLength = contract.MinShiftLengthHours.Value ?? 0m;

            int qualifying = shiftsInWeek.Count(x => x.PaidHours > 0 && x.PaidHours >= minLength);

            if (qualifying < minShifts)
            {
                string lengthText = minLength > 0 ? $" of {Hours(minLength)} or more" : string.Empty;
                string shiftWord = minShifts == 1 ? "shift" : "shifts";

                warnings.Add(new ContractWarning(userId, weekStart, ContractWarningKind.BelowMinShifts,
                    $"Needs at least {minShifts} {shiftWord}{lengthText} this week (has {qualifying})."));
            }
        }

        if (contract.MinHoursPerWeek.Value is decimal minHours && minHours > 0 && totalHours < minHours)
        {
            warnings.Add(new ContractWarning(userId, weekStart, ContractWarningKind.BelowMinHours,
                $"Scheduled {Hours(totalHours)}, below the {Hours(minHours)} weekly minimum."));
        }

        if (contract.MaxHoursPerWeek.Value is decimal maxHours && totalHours > maxHours)
        {
            warnings.Add(new ContractWarning(userId, weekStart, ContractWarningKind.AboveMaxHours,
                $"Scheduled {Hours(totalHours)}, over the {Hours(maxHours)} weekly maximum."));
        }

        return warnings;
    }

    // True when approved time off covers all seven days of the Monday-starting week.
    public static bool IsWholeWeekOff(DateOnly weekStart, IReadOnlyCollection<TimeOffRequest> approvedTimeOff) =>
        approvedTimeOff.Count > 0
        && Enumerable.Range(0, 7).All(i => approvedTimeOff.Any(x => x.Covers(weekStart.AddDays(i))));

    // One warning per shift that falls on a day of approved time off.
    public static List<ContractWarning> ShiftsDuringTimeOff(string userId, DateOnly weekStart, IReadOnlyCollection<Shift> shiftsInWeek, IReadOnlyCollection<TimeOffRequest> approvedTimeOff)
    {
        List<ContractWarning> warnings = [];

        if (approvedTimeOff.Count == 0)
        {
            return warnings;
        }

        foreach (Shift shift in shiftsInWeek.OrderBy(x => x.StartUtc))
        {
            DateOnly day = RotaTime.LocalDate(shift.StartUtc);
            TimeOffRequest? timeOff = approvedTimeOff.FirstOrDefault(x => x.Covers(day));

            if (timeOff is not null)
            {
                warnings.Add(new ContractWarning(userId, weekStart, ContractWarningKind.ShiftDuringTimeOff,
                    $"On the rota on {day.ToString("ddd d MMM", RotaFormat.Uk)} during approved {timeOff.TimeOffType?.Name.ToLower() ?? "time off"}."));
            }
        }

        return warnings;
    }

    private static string Hours(decimal hours) => RotaFormat.Hours(hours);
}
