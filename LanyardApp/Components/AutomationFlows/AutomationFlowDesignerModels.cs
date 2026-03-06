namespace Lanyard.App.Components.AutomationFlows;

public enum AutomationStepKind
{
    Trigger = 0,
    Action = 1
}

public sealed class AutomationFlowDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<AutomationFlowStepDefinition> Steps { get; set; } = [];
}

public sealed class AutomationFlowStepDefinition
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public int SortOrder { get; set; }
    public AutomationStepTemplateDefinition? Template { get; set; }
    public List<AutomationStepParameterValueDefinition> ParameterValues { get; set; } = [];
}

public sealed class AutomationStepTemplateDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AutomationStepKind Kind { get; set; }
    public List<AutomationStepTemplateParameterDefinition> Parameters { get; set; } = [];
}

public sealed class AutomationStepTemplateParameterDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DataType { get; set; } = "String";
    public bool IsRequired { get; set; }
}

public sealed class AutomationStepParameterValueDefinition
{
    public Guid ParameterId { get; set; }
    public string Value { get; set; } = string.Empty;
}
