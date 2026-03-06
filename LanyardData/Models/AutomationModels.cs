namespace Lanyard.Infrastructure.Models;

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
