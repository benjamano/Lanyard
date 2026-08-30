using Lanyard.Infrastructure.Enum;
using Lanyard.Shared.Enum;

namespace Lanyard.Infrastructure.Models;

public class AutomationRule
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public Guid TriggerClientId { get; set; }
    public Client? TriggerClient { get; set; }

    public AutomationTriggerType TriggerType { get; set; } = AutomationTriggerType.GameStatusTransition;

    // Only meaningful when TriggerType is GameStatusTransition; ignored for ClientIdle rules.
    public GameStatus TriggerEvent { get; set; }

    // Required when TriggerType is ClientIdle, null otherwise. AutomationRuleService rejects a
    // ClientIdle rule without one rather than letting a rule that can never fire be saved.
    public int? IdleThresholdMinutes { get; set; }

    // Required when TriggerType is Scheduled, null otherwise. Server-local wall-clock time -
    // matches HallOfFamePeriodExtensions/Client.AutoRestartTimeOfDay, since there is no venue
    // timezone concept in this app and local is the basis staff actually experience.
    public TimeOnly? ScheduledTimeOfDay { get; set; }

    // Meaningful only when TriggerType is Scheduled. CSV of System.DayOfWeek integer values
    // (e.g. "1,2,3,4,5"); null or empty means every day. See AutomationScheduleDays for the
    // shared parse/serialize/match logic.
    public string? ScheduledDaysOfWeek { get; set; }

    public bool IsActive { get; set; }
    public bool IsEnabled { get; set; } = true;

    public DateTime CreateDate { get; set; }
    public DateTime? LastUpdateDate { get; set; }

    public virtual List<AutomationRuleAction> Actions { get; set; } = [];
    public virtual List<AutomationRuleExecution> Executions { get; set; } = [];
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
