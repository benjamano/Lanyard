using Lanyard.Shared.Enum;

namespace Lanyard.Infrastructure.Models;

public class AutomationRule
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public Guid TriggerClientId { get; set; }
    public Client? TriggerClient { get; set; }

    public GameStatus TriggerEvent { get; set; }

    public bool IsActive { get; set; }

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
