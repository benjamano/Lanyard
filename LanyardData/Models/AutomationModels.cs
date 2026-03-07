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
public class AutomationStepTemplate
{
    public Guid Id { get; set; }

    public required string Name { get; set; }
    public string? Category { get; set; }

    public bool IsTrigger { get; set; }
    public bool IsAction { get; set; }

    public bool IsActive { get; set; }

    public virtual ICollection<AutomationStepTemplateParameter> Parameters { get; set; } = [];
    public virtual ICollection<AutomationStep> AutomationSteps { get; set; } = [];
}

public class AutomationStepTemplateParameter
{
    public Guid Id { get; set; }

    public required Guid TemplateId { get; set; }
    public AutomationStepTemplate? Template { get; set; }

    public required string Name { get; set; }
    public required string DataType { get; set; }

    public bool IsRequired { get; set; }
    public bool IsActive { get; set; }

    public virtual ICollection<AutomationStepParameterValue> StepParameterValues { get; set; } = [];
}

public class AutomationFlow
{
    public Guid Id { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public Guid? ClientId { get; set; }
    public Client? Client { get; set; }

    public string? TriggerEventName { get; set; }
    public string? TriggerConditions { get; set; }

    public DateTime? LastRunUtc { get; set; }
    public string? LastRunResult { get; set; }

    public virtual ICollection<AutomationStep> Steps { get; set; } = [];
    public virtual ICollection<FlowTriggerBinding> TriggerBindings { get; set; } = [];
}

public class AutomationStep
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
    public Guid StepTypeTemplateId { get; set; }
    public AutomationStepTemplate? StepTypeTemplate { get; set; }

    public int SortOrder { get; set; }
    public bool IsActive { get; set; }

    public virtual ICollection<AutomationStepParameterValue> ParameterValues { get; set; } = [];
}

public class AutomationStepParameterValue
{
    public Guid Id { get; set; }

    public Guid AutomationStepId { get; set; }
    public AutomationStep? AutomationStep { get; set; }

    public Guid ParameterId { get; set; }
    public AutomationStepTemplateParameter? Parameter { get; set; }

    public string? Value { get; set; }
}

public class FlowTriggerBinding
{
    public Guid Id { get; set; }

    public Guid AutomationFlowId { get; set; }
    public AutomationFlow? AutomationFlow { get; set; }

    public required string EventName { get; set; }
    public string? Conditions { get; set; }

    public bool IsActive { get; set; }
}
