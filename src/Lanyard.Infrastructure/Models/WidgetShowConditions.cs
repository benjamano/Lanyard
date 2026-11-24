using System.Text.Json;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models;

/// <summary>
/// A dashboard widget's "Show only when…" conditions, stored as a small JSON list on the widget
/// (DashboardWidget.ShowConditionsJson). They are the same conditions automation rules use (time of
/// day, days, a kiosk in a game, a location open), so AutomationConditionEvaluator decides them and
/// AutomationRuleService.ValidateConditions checks them. Null or empty means always shown.
/// </summary>
public static class WidgetShowConditions
{
    private sealed record Stored(AutomationConditionType Type, string Parameters);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The conditions in order, shaped as AutomationRuleCondition for the evaluator and editor.
    /// Unreadable JSON gives a single condition that can never pass, so a damaged widget stays hidden
    /// rather than showing when it shouldn't.</summary>
    public static List<AutomationRuleCondition> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            List<Stored>? stored = JsonSerializer.Deserialize<List<Stored>>(json, Options);

            return stored is null
                ? []
                : [.. stored.Select((x, i) => new AutomationRuleCondition
                {
                    Id = Guid.NewGuid(),
                    ConditionType = x.Type,
                    ParametersJson = x.Parameters,
                    SortOrder = i,
                    IsActive = true
                })];
        }
        catch (JsonException)
        {
            return [new AutomationRuleCondition { Id = Guid.NewGuid(), ConditionType = (AutomationConditionType)(-1), ParametersJson = "{}", IsActive = true }];
        }
    }

    /// <summary>The JSON to store, or null when there are no conditions.</summary>
    public static string? Serialize(IEnumerable<AutomationRuleCondition> conditions)
    {
        List<Stored> stored = [.. conditions
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .Select(x => new Stored(x.ConditionType, x.ParametersJson))];

        return stored.Count == 0 ? null : JsonSerializer.Serialize(stored, Options);
    }

    /// <summary>A short summary for the dashboard editor: "18:00–23:59", "Sat, Sun", "3 conditions".</summary>
    public static string? Summary(string? json)
    {
        List<AutomationRuleCondition> conditions = Parse(json);

        if (conditions.Count == 0)
        {
            return null;
        }

        if (conditions.Count > 1)
        {
            return $"{conditions.Count} conditions";
        }

        AutomationRuleCondition only = conditions[0];

        return only.ConditionType switch
        {
            AutomationConditionType.TimeWindow when AutomationConditionParameters.TryParse(only.ParametersJson, out TimeWindowConditionParameters? window) =>
                $"{window.Start:HH\\:mm}–{window.End:HH\\:mm}",
            AutomationConditionType.DaysOfWeek => "Some days",
            AutomationConditionType.ClientGameStatus => "Kiosk game",
            AutomationConditionType.DuringOpeningHours => "Open/closed",
            _ => "1 condition"
        };
    }
}
