using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
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

    // "Record a number in °C, acceptable 0–5" for the editor's task list.
    public static string ResponseLabel(ChecklistTask task)
    {
        string label = ResponseLabel(task.Response, task.Unit);

        return task.Response == ChecklistResponse.Number && ChecklistRules.RangeText(task.MinReading, task.MaxReading, null) is string range
            ? $"{label}, acceptable {range}"
            : label;
    }

    // What a reading outside its range does, in the editor's words.
    public static string ReadingAlertLabel(ChecklistReadingAlert alert) => alert switch
    {
        ChecklistReadingAlert.AlertOnly => "Raise an alert only",
        ChecklistReadingAlert.None => "Don't alert, just flag it on the task",
        _ => "Raise an alert and notify Managers"
    };

    // A ticked reading's verdict, always as words next to an icon.
    public static string ReadingStatusLabel(ChecklistReadingStatus status) => status switch
    {
        ChecklistReadingStatus.TooLow => "Too low",
        ChecklistReadingStatus.TooHigh => "Too high",
        ChecklistReadingStatus.InRange => "In range",
        _ => string.Empty
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
