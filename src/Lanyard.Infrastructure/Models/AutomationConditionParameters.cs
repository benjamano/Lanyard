using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models;

// Typed shapes of AutomationRuleCondition.ParametersJson, one per AutomationConditionType.

/// <summary>Venue-local times. An End before Start wraps past midnight (e.g. 22:00-02:00).</summary>
public sealed record TimeWindowConditionParameters(TimeOnly Start, TimeOnly End);

/// <summary>Same CSV format as AutomationRule.ScheduledDaysOfWeek - see AutomationScheduleDays.</summary>
public sealed record DaysOfWeekConditionParameters(string? Days);

/// <summary>Passes when the kiosk's live in-game state equals InGame.</summary>
public sealed record ClientGameStatusConditionParameters(Guid ClientId, bool InGame);

/// <summary>Open true passes while the location is open; false passes while it's closed.</summary>
public sealed record OpeningHoursConditionParameters(int LocationId, bool Open);

/// <summary>
/// Shared parse/serialize/validate logic for condition parameters, so the engine, the rule
/// service and the edit dialog all agree on the same JSON and the same rules.
/// </summary>
public static class AutomationConditionParameters
{
    public static string Serialize<T>(T parameters) where T : class
    {
        return JsonSerializer.Serialize(parameters);
    }

    public static bool TryParse<T>(string? json, [NotNullWhen(true)] out T? parameters) where T : class
    {
        try
        {
            parameters = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json);
            return parameters != null;
        }
        catch (JsonException)
        {
            parameters = null;
            return false;
        }
    }

    /// <summary>
    /// Returns a user-facing error, or null when the condition is well formed. Whether a kiosk or
    /// location actually exists in the caller's company is checked separately by AutomationRuleService,
    /// since that needs the database.
    /// </summary>
    public static string? Validate(AutomationConditionType conditionType, string? parametersJson)
    {
        switch (conditionType)
        {
            case AutomationConditionType.TimeWindow:
                if (!TryParse(parametersJson, out TimeWindowConditionParameters? window))
                {
                    return "A time window condition is not valid.";
                }

                // Equal start and end is ambiguous (always or never), so make the user choose.
                if (window.Start == window.End)
                {
                    return "A time window's start and end can't be the same time.";
                }

                return null;

            case AutomationConditionType.DaysOfWeek:
                if (!TryParse(parametersJson, out DaysOfWeekConditionParameters? days))
                {
                    return "A days of week condition is not valid.";
                }

                // Unlike a schedule, a blank list here would be a condition that checks nothing.
                if (string.IsNullOrWhiteSpace(days.Days))
                {
                    return "Pick at least one day for a days of week condition.";
                }

                if (!AutomationScheduleDays.TryParse(days.Days, out _))
                {
                    return "A days of week condition's days are not valid.";
                }

                return null;

            case AutomationConditionType.ClientGameStatus:
                if (!TryParse(parametersJson, out ClientGameStatusConditionParameters? client))
                {
                    return "A kiosk game status condition is not valid.";
                }

                if (client.ClientId == Guid.Empty)
                {
                    return "Pick a kiosk for the game status condition.";
                }

                return null;

            case AutomationConditionType.DuringOpeningHours:
                if (!TryParse(parametersJson, out OpeningHoursConditionParameters? hours))
                {
                    return "An opening hours condition is not valid.";
                }

                if (hours.LocationId <= 0)
                {
                    return "Pick a location for the opening hours condition.";
                }

                return null;

            default:
                return "Unknown condition type.";
        }
    }
}
