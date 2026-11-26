using Lanyard.Infrastructure.Enum;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.App.Components.Checklists;

// Wording shared by the checklist pages, so a task reads the same everywhere.
public static class ChecklistDisplay
{
    public static string ResponseLabel(ChecklistResponse response, string? unit) => response switch
    {
        ChecklistResponse.TickWithNote => "Tick, with an optional note",
        ChecklistResponse.Number => string.IsNullOrWhiteSpace(unit) ? "Record a number" : $"Record a number in {unit}",
        _ => "Tick"
    };

    // Always words, never colour alone. A missed checklist today is "Overdue": it can still be done.
    public static string StatusLabel(ChecklistRunStatus status, bool isToday) => status switch
    {
        ChecklistRunStatus.Complete => "Done",
        ChecklistRunStatus.InProgress => "In progress",
        ChecklistRunStatus.Missed => isToday ? "Overdue" : "Missed",
        _ => "Not started"
    };

    public static BadgeColor StatusColor(ChecklistRunStatus status) => status switch
    {
        ChecklistRunStatus.Complete => BadgeColor.Success,
        ChecklistRunStatus.InProgress => BadgeColor.Informative,
        ChecklistRunStatus.Missed => BadgeColor.Danger,
        _ => BadgeColor.Subtle
    };
}
