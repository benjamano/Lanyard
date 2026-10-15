using Lanyard.Infrastructure.Enum;
using Lanyard.Shared.Enum;

namespace Lanyard.Infrastructure.Models;

public class AutomationRule : ICompanyOwned
{
    public Guid Id { get; set; }
    public int CompanyId { get; set; }

    public required string Name { get; set; }

    public Guid TriggerClientId { get; set; }
    public Client? TriggerClient { get; set; }

    public AutomationTriggerType TriggerType { get; set; } = AutomationTriggerType.GameStatusTransition;

    // Only meaningful when TriggerType is GameStatusTransition; ignored for ClientIdle rules.
    public GameStatus TriggerEvent { get; set; }

    // Required when TriggerType is ClientIdle, null otherwise. AutomationRuleService rejects a
    // ClientIdle rule without one rather than letting a rule that can never fire be saved.
    public int? IdleThresholdMinutes { get; set; }

    // Required when TriggerType is Scheduled, null otherwise. Venue wall-clock time (UK, see
    // RotaTime) - the basis staff actually experience, whatever zone the server runs in.
    public TimeOnly? ScheduledTimeOfDay { get; set; }

    // Meaningful only when TriggerType is Scheduled. CSV of System.DayOfWeek integer values
    // (e.g. "1,2,3,4,5"); null or empty means every day. See AutomationScheduleDays for the
    // shared parse/serialize/match logic.
    public string? ScheduledDaysOfWeek { get; set; }

    // Seconds for TimeRemainingBelow, points for ScoreThreshold; null for every other trigger type.
    public int? TriggerThreshold { get; set; }

    // ScoreThreshold only: whose score counts. Null otherwise.
    public AutomationScoreScope? TriggerScoreScope { get; set; }

    // ScoreThreshold only: limit to one team's players (AnyPlayer) or pick the team (Team). Null
    // means any team.
    public Team? TriggerTeam { get; set; }

    public bool IsActive { get; set; }
    public bool IsEnabled { get; set; } = true;

    public DateTime CreateDate { get; set; }
    public DateTime? LastUpdateDate { get; set; }

    public virtual List<AutomationRuleAction> Actions { get; set; } = [];

    // Extra "only if" checks, all of which must pass when the trigger fires (AND). A rule with no
    // active conditions behaves exactly as rules did before conditions existed.
    public virtual List<AutomationRuleCondition> Conditions { get; set; } = [];

    public virtual List<AutomationRuleExecution> Executions { get; set; } = [];
}

public class AutomationRuleCondition
{
    public Guid Id { get; set; }

    public Guid AutomationRuleId { get; set; }
    public AutomationRule? AutomationRule { get; set; }

    public AutomationConditionType ConditionType { get; set; }

    // Shape depends on ConditionType - see AutomationConditionParameters for the typed records.
    public string ParametersJson { get; set; } = "{}";

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }
}

public class AutomationRuleAction
{
    public Guid Id { get; set; }

    public Guid AutomationRuleId { get; set; }
    public AutomationRule? AutomationRule { get; set; }

    public required string ActionType { get; set; }
    public string ParametersJson { get; set; } = "{}";

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }
}

public class AutomationRuleExecution
{
    public Guid Id { get; set; }

    public Guid AutomationRuleId { get; set; }
    public AutomationRule? AutomationRule { get; set; }

    public required string RuleName { get; set; }

    public DateTime ExecutedAt { get; set; }
    public string TriggerEvent { get; set; } = string.Empty;
    public Guid TriggerClientId { get; set; }

    public bool OverallSuccess { get; set; }

    // Set when the trigger fired but a condition wasn't met, so no actions ran. Such a row has
    // OverallSuccess false and no action rows, and must not be read as a failure or as "fired".
    public string? SkipReason { get; set; }

    public virtual List<AutomationRuleActionExecution> ActionExecutions { get; set; } = [];
}

public class AutomationRuleActionExecution
{
    public Guid Id { get; set; }

    public Guid AutomationRuleExecutionId { get; set; }
    public AutomationRuleExecution? AutomationRuleExecution { get; set; }

    public Guid AutomationRuleActionId { get; set; }
    public AutomationRuleAction? AutomationRuleAction { get; set; }

    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
