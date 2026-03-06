namespace Lanyard.Application.Services.Automation;

public interface IAutomationFlowRunLogService
{
    Task RecordRunAsync(AutomationFlowRunRecord runRecord);
    IReadOnlyList<AutomationFlowRunRecord> GetRecentRuns(int limit = 100);
    IReadOnlyList<AutomationFlowLatestStatus> GetLatestStatusByFlow();
    Task<AutomationFlowRunRecord> RunManualTestTriggerAsync(Guid clientId, string triggerPayload, IEnumerable<Guid> matchedFlowIds, CancellationToken cancellationToken = default);
}
