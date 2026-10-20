#nullable enable

using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services;

/// <summary>The engine's cached view of a kiosk, for checking conditions that name one.</summary>
public sealed record AutomationClientInfo(int CompanyId, string Name);

/// <summary>The engine's cached view of a location and its active opening hours.</summary>
public sealed record AutomationLocationInfo(int CompanyId, string Name, IReadOnlyList<LocationOpeningHours> Hours);

/// <param name="IsMisconfigured">
/// True when the condition can never pass as saved (bad JSON, a deleted kiosk, a kiosk in another
/// company) rather than simply not being met right now. Logged as a warning, not information.
/// </param>
public sealed record AutomationConditionCheck(bool Passed, string? Reason, bool IsMisconfigured)
{
    public static readonly AutomationConditionCheck Pass = new(true, null, false);

    public static AutomationConditionCheck NotMet(string reason) => new(false, reason, false);

    public static AutomationConditionCheck Misconfigured(string reason) => new(false, reason, true);
}

/// <summary>
/// Pure so every condition can be unit tested at fixed instants, without the engine, its timers or
/// a database.
/// </summary>
/// <remarks>
/// Times and days are venue-local (RotaTime.VenueZone), not server-local: the production container
/// runs in UTC, so DateTime.Now would put every summer window an hour late. Anything that can't be
/// parsed fails closed - a broken condition must stop the rule, not let it run at all hours.
/// </remarks>
public static class AutomationConditionEvaluator
{
    /// <summary>All active conditions must pass (AND). The first one that doesn't is reported.</summary>
    public static AutomationConditionCheck Evaluate(
        IEnumerable<AutomationRuleCondition> conditions,
        int ruleCompanyId,
        DateTime nowUtc,
        Func<Guid, AutomationClientInfo?> clientOf,
        Func<Guid, bool> isInGame,
        Func<int, AutomationLocationInfo?>? locationOf = null)
    {
        DateTime venueLocal = RotaTime.ToVenueLocal(nowUtc);
        Func<int, AutomationLocationInfo?> findLocation = locationOf ?? (_ => null);

        foreach (AutomationRuleCondition condition in conditions.Where(c => c.IsActive).OrderBy(c => c.SortOrder))
        {
            AutomationConditionCheck check = EvaluateOne(condition, ruleCompanyId, venueLocal, clientOf, isInGame, findLocation);

            if (!check.Passed)
            {
                return check;
            }
        }

        return AutomationConditionCheck.Pass;
    }

    /// <summary>
    /// Start is inclusive and end exclusive, so back-to-back windows (18:00-22:00, 22:00-02:00)
    /// never both match. An end at or before the start wraps past midnight.
    /// </summary>
    public static bool IsWithinWindow(TimeOnly start, TimeOnly end, TimeOnly now)
    {
        if (start < end)
        {
            return now >= start && now < end;
        }

        if (start > end)
        {
            return now >= start || now < end;
        }

        // Rejected at save; fail closed if one slips through anyway.
        return false;
    }

    private static AutomationConditionCheck EvaluateOne(
        AutomationRuleCondition condition,
        int ruleCompanyId,
        DateTime venueLocal,
        Func<Guid, AutomationClientInfo?> clientOf,
        Func<Guid, bool> isInGame,
        Func<int, AutomationLocationInfo?> locationOf)
    {
        switch (condition.ConditionType)
        {
            case AutomationConditionType.TimeWindow:
            {
                if (!AutomationConditionParameters.TryParse(condition.ParametersJson, out TimeWindowConditionParameters? window)
                    || window.Start == window.End)
                {
                    return AutomationConditionCheck.Misconfigured("The time window condition is not valid");
                }

                TimeOnly now = TimeOnly.FromDateTime(venueLocal);

                return IsWithinWindow(window.Start, window.End, now)
                    ? AutomationConditionCheck.Pass
                    : AutomationConditionCheck.NotMet($"Outside {window.Start:HH:mm}-{window.End:HH:mm} (it was {now:HH:mm})");
            }

            case AutomationConditionType.DaysOfWeek:
            {
                if (!AutomationConditionParameters.TryParse(condition.ParametersJson, out DaysOfWeekConditionParameters? days)
                    || string.IsNullOrWhiteSpace(days.Days))
                {
                    return AutomationConditionCheck.Misconfigured("The days of week condition is not valid");
                }

                return AutomationScheduleDays.Matches(days.Days, venueLocal.DayOfWeek)
                    ? AutomationConditionCheck.Pass
                    : AutomationConditionCheck.NotMet($"{venueLocal.DayOfWeek} isn't one of the chosen days");
            }

            case AutomationConditionType.ClientGameStatus:
            {
                if (!AutomationConditionParameters.TryParse(condition.ParametersJson, out ClientGameStatusConditionParameters? wanted)
                    || wanted.ClientId == Guid.Empty)
                {
                    return AutomationConditionCheck.Misconfigured("The kiosk game status condition is not valid");
                }

                AutomationClientInfo? client = clientOf(wanted.ClientId);

                if (client == null)
                {
                    return AutomationConditionCheck.Misconfigured("The kiosk in the game status condition no longer exists");
                }

                // The engine runs unfiltered (system context), so nothing else stops a rule from
                // reading another company's kiosk.
                if (client.CompanyId != ruleCompanyId)
                {
                    return AutomationConditionCheck.Misconfigured("The kiosk in the game status condition belongs to another company");
                }

                bool inGame = isInGame(wanted.ClientId);

                if (inGame == wanted.InGame)
                {
                    return AutomationConditionCheck.Pass;
                }

                return AutomationConditionCheck.NotMet(inGame ? $"{client.Name} is in a game" : $"{client.Name} isn't in a game");
            }

            case AutomationConditionType.DuringOpeningHours:
            {
                if (!AutomationConditionParameters.TryParse(condition.ParametersJson, out OpeningHoursConditionParameters? wanted)
                    || wanted.LocationId <= 0)
                {
                    return AutomationConditionCheck.Misconfigured("The opening hours condition is not valid");
                }

                AutomationLocationInfo? location = locationOf(wanted.LocationId);

                if (location == null)
                {
                    return AutomationConditionCheck.Misconfigured("The location in the opening hours condition no longer exists");
                }

                // Locations aren't tenant-filtered rows at all, so this is the only company check.
                if (location.CompanyId != ruleCompanyId)
                {
                    return AutomationConditionCheck.Misconfigured("The location in the opening hours condition belongs to another company");
                }

                // No hours set reads as closed rather than misconfigured: it's a normal state for a
                // new location, and "outside opening hours" rules should still run.
                bool isOpen = OpeningHoursRules.IsOpen(location.Hours, venueLocal);

                if (isOpen == wanted.Open)
                {
                    return AutomationConditionCheck.Pass;
                }

                if (isOpen)
                {
                    return AutomationConditionCheck.NotMet($"{location.Name} is open");
                }

                return AutomationConditionCheck.NotMet(location.Hours.Any(h => h.IsActive)
                    ? $"{location.Name} is closed (it was {venueLocal:HH:mm} on {venueLocal.DayOfWeek})"
                    : $"{location.Name} has no opening hours set");
            }

            default:
                return AutomationConditionCheck.Misconfigured($"Unknown condition type {(int)condition.ConditionType}");
        }
    }
}
