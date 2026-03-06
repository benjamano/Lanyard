namespace Lanyard.Infrastructure.Models;

public class FlowTriggerEvent
{
    public required string TriggerKey { get; set; }
    public Guid? ClientId { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = [];
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public class AutomationFlow
{
    public Guid Id { get; set; }

    public required string Name { get; set; }
    public required string TriggerKey { get; set; }

    public Guid? ClientId { get; set; }

    /// <summary>
    /// Optional condition in the form key==value;otherKey!=otherValue.
    /// </summary>
    public string? ConditionExpression { get; set; }

    public bool ContinueOnStepFailure { get; set; }
    public bool IsActive { get; set; }

    public DateTime CreateDate { get; set; }
    public DateTime? LastUpdateDate { get; set; }

    public virtual List<AutomationFlowStep> Steps { get; set; } = [];
    public virtual List<AutomationFlowRun> Runs { get; set; } = [];
}

public class AutomationFlowStep
{
    public Guid Id { get; set; }

    public Guid AutomationFlowId { get; set; }
    public AutomationFlow? AutomationFlow { get; set; }

    public int SortOrder { get; set; }
    public required string ActionType { get; set; }
    public string? ActionPayloadJson { get; set; }

    public bool ContinueOnFailure { get; set; }
    public bool IsActive { get; set; }
}

public class AutomationFlowRun
{
    public Guid Id { get; set; }

    public Guid AutomationFlowId { get; set; }
    public AutomationFlow? AutomationFlow { get; set; }

    public required string TriggerKey { get; set; }
    public Guid? ClientId { get; set; }

    public bool IsSuccess { get; set; }
    public int ExecutedSteps { get; set; }
    public int FailedSteps { get; set; }

    public string? Summary { get; set; }

    public DateTime RunStartedAtUtc { get; set; }
    public DateTime RunCompletedAtUtc { get; set; }

    public virtual List<AutomationFlowRunStepResult> StepResults { get; set; } = [];
}

public class AutomationFlowRunStepResult
{
    public Guid Id { get; set; }

    public Guid AutomationFlowRunId { get; set; }
    public AutomationFlowRun? AutomationFlowRun { get; set; }

    public Guid AutomationFlowStepId { get; set; }
    public int SortOrder { get; set; }

    public bool IsSuccess { get; set; }
    public string? Message { get; set; }

    public DateTime LoggedAtUtc { get; set; }
}
