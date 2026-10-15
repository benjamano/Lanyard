namespace Lanyard.Infrastructure.Enum;

/// <summary>
/// An "only if" check on an automation rule. Stored as an int on AutomationRuleCondition, so
/// existing values must never be renumbered.
/// </summary>
public enum AutomationConditionType
{
    /// <summary>
    /// Only between two venue-local times of day. An end before the start wraps past midnight.
    /// </summary>
    TimeWindow = 0,

    /// <summary>
    /// Only on the chosen venue-local days of the week.
    /// </summary>
    DaysOfWeek = 1,

    /// <summary>
    /// Only if a given kiosk is (or isn't) in a game right now.
    /// </summary>
    ClientGameStatus = 2
}
