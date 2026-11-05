using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Scheduling;

// Checks on a shift template before it's saved. Pure, so the dialog and the service agree and it
// can be tested without a database.
public static class ShiftTemplateRules
{
    public const int MaxNameLength = 80;
    public const int MaxHeadcount = 50;

    public static string? Validate(ShiftTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.Name))
        {
            return "Give the shift a name, such as Saturday AM.";
        }

        if (template.Name.Trim().Length > MaxNameLength)
        {
            return $"Keep the name to {MaxNameLength} characters or fewer.";
        }

        if (template.StartTime == template.EndTime)
        {
            return "Choose different start and end times.";
        }

        if (template.BreakMinutes < 0)
        {
            return "A break can't be negative.";
        }

        if (template.BreakMinutes >= template.Length.TotalMinutes)
        {
            return "The break must be shorter than the shift.";
        }

        if (template.EffectiveFrom is DateOnly from && template.EffectiveTo is DateOnly to && to < from)
        {
            return "The end date must be on or after the start date.";
        }

        if (template.Slots.Count == 0)
        {
            return "Say who the shift needs - add at least one position.";
        }

        if (template.Slots.Any(x => x.Headcount < 1 || x.Headcount > MaxHeadcount))
        {
            return $"Each position needs between 1 and {MaxHeadcount} people.";
        }

        if (template.Slots.GroupBy(x => x.StaffPositionId).Any(g => g.Count() > 1))
        {
            return "List each position once and set how many people it needs.";
        }

        return null;
    }
}
