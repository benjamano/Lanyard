namespace Lanyard.Application.Services.Automation;

public class AutomationFlowRunRecord
{
    public Guid RunId { get; set; }
    public Guid? ClientId { get; set; }
    public string TriggerPayload { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorText { get; set; }
    public List<Guid> MatchedFlowIds { get; set; } = [];
    public List<AutomationFlowRunStepRecord> Steps { get; set; } = [];
}

public class AutomationFlowRunStepRecord
{
    public Guid StepId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public string ResultText { get; set; } = string.Empty;
    public string? ErrorText { get; set; }
    public DateTime CompletedUtc { get; set; }
}

public class AutomationFlowLatestStatus
{
    public Guid FlowId { get; set; }
    public DateTime LastRunUtc { get; set; }
    public bool IsSuccess { get; set; }
    public string? LatestError { get; set; }
    public string TriggerPayload { get; set; } = string.Empty;
    public Guid? LastClientId { get; set; }
    public Guid RunId { get; set; }
}
