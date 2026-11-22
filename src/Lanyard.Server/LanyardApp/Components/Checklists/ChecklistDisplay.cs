using Lanyard.Infrastructure.Enum;

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
}
