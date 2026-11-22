using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.App.Components.Conditions;

// One "only if" condition as the editor holds it: every type's fields at once, so switching type
// in the dropdown doesn't lose what was typed. Shared by automation rules and dashboard widgets.
public sealed class ConditionRow
{
    public Guid? ExistingConditionId { get; set; }
    public AutomationConditionType ConditionType { get; set; } = AutomationConditionType.TimeWindow;
    public TimeOnly Start { get; set; } = new(18, 0);
    public TimeOnly End { get; set; } = new(22, 0);
    public HashSet<DayOfWeek> Days { get; set; } = [];
    public string ClientIdString { get; set; } = string.Empty;
    public bool InGame { get; set; }
    public string LocationIdString { get; set; } = string.Empty;
    public bool Open { get; set; } = true;
}

// To and from the stored condition shape (type + JSON parameters, see AutomationConditionParameters).
public static class ConditionRows
{
    public static List<ConditionRow> From(IEnumerable<AutomationRuleCondition> conditions) =>
        [.. conditions.Where(c => c.IsActive).OrderBy(c => c.SortOrder).Select(From)];

    // Each row in order, as AutomationRuleCondition (the type both automation rules and widget
    // conditions are validated as - see AutomationRuleService.ValidateConditions).
    public static List<AutomationRuleCondition> ToConditions(IReadOnlyList<ConditionRow> rows)
    {
        List<AutomationRuleCondition> conditions = [];

        for (int i = 0; i < rows.Count; i++)
        {
            ConditionRow row = rows[i];

            string parametersJson = row.ConditionType switch
            {
                AutomationConditionType.TimeWindow => AutomationConditionParameters.Serialize(new TimeWindowConditionParameters(row.Start, row.End)),
                AutomationConditionType.DaysOfWeek => AutomationConditionParameters.Serialize(new DaysOfWeekConditionParameters(AutomationScheduleDays.Serialize(row.Days))),
                AutomationConditionType.DuringOpeningHours => AutomationConditionParameters.Serialize(new OpeningHoursConditionParameters(
                    int.TryParse(row.LocationIdString, out int locationId) ? locationId : 0,
                    row.Open)),
                _ => AutomationConditionParameters.Serialize(new ClientGameStatusConditionParameters(
                    Guid.TryParse(row.ClientIdString, out Guid clientId) ? clientId : Guid.Empty,
                    row.InGame))
            };

            conditions.Add(new AutomationRuleCondition
            {
                Id = row.ExistingConditionId ?? Guid.NewGuid(),
                ConditionType = row.ConditionType,
                ParametersJson = parametersJson,
                SortOrder = i,
                IsActive = true
            });
        }

        return conditions;
    }

    public static ConditionRow From(AutomationRuleCondition condition) => From(condition.Id, condition.ConditionType, condition.ParametersJson);

    public static ConditionRow From(Guid id, AutomationConditionType type, string parametersJson)
    {
        ConditionRow row = new()
        {
            ExistingConditionId = id,
            ConditionType = type
        };

        switch (type)
        {
            case AutomationConditionType.TimeWindow when AutomationConditionParameters.TryParse(parametersJson, out TimeWindowConditionParameters? window):
                row.Start = window.Start;
                row.End = window.End;
                break;

            case AutomationConditionType.DaysOfWeek when AutomationConditionParameters.TryParse(parametersJson, out DaysOfWeekConditionParameters? days)
                && !string.IsNullOrWhiteSpace(days.Days)
                && AutomationScheduleDays.TryParse(days.Days, out List<DayOfWeek> parsedDays):
                row.Days = [.. parsedDays];
                break;

            case AutomationConditionType.ClientGameStatus when AutomationConditionParameters.TryParse(parametersJson, out ClientGameStatusConditionParameters? kiosk):
                row.ClientIdString = kiosk.ClientId == Guid.Empty ? string.Empty : kiosk.ClientId.ToString();
                row.InGame = kiosk.InGame;
                break;

            case AutomationConditionType.DuringOpeningHours when AutomationConditionParameters.TryParse(parametersJson, out OpeningHoursConditionParameters? hours):
                row.LocationIdString = hours.LocationId > 0 ? hours.LocationId.ToString() : string.Empty;
                row.Open = hours.Open;
                break;
        }

        return row;
    }
}
