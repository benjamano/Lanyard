using System.Collections.Concurrent;

namespace Lanyard.Application.Services.Automation;

public class AutomationFlowRunLogService : IAutomationFlowRunLogService
{
    private const int MaxCacheSize = 500;
    private readonly ConcurrentQueue<AutomationFlowRunRecord> _runs = new();
    private readonly object _sync = new();

    public Task RecordRunAsync(AutomationFlowRunRecord runRecord)
    {
        if (runRecord.RunId == Guid.Empty)
        {
            runRecord.RunId = Guid.NewGuid();
        }

        _runs.Enqueue(runRecord);

        lock (_sync)
        {
            while (_runs.Count > MaxCacheSize)
            {
                _runs.TryDequeue(out _);
            }
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<AutomationFlowRunRecord> GetRecentRuns(int limit = 100)
    {
        int safeLimit = Math.Max(1, limit);

        List<AutomationFlowRunRecord> runs = _runs.ToList();
        List<AutomationFlowRunRecord> ordered = runs
            .OrderByDescending(x => x.CompletedUtc)
            .Take(safeLimit)
            .ToList();

        return ordered;
    }

    public IReadOnlyList<AutomationFlowLatestStatus> GetLatestStatusByFlow()
    {
        List<AutomationFlowRunRecord> runs = _runs.ToList();

        List<AutomationFlowLatestStatus> statuses = runs
            .SelectMany(run => run.MatchedFlowIds.Select(flowId => new AutomationFlowLatestStatus
            {
                FlowId = flowId,
                LastRunUtc = run.CompletedUtc,
                IsSuccess = run.IsSuccess,
                LatestError = run.ErrorText,
                TriggerPayload = run.TriggerPayload,
                LastClientId = run.ClientId,
                RunId = run.RunId
            }))
            .GroupBy(x => x.FlowId)
            .Select(group => group
                .OrderByDescending(x => x.LastRunUtc)
                .First())
            .OrderByDescending(x => x.LastRunUtc)
            .ToList();

        return statuses;
    }

    public async Task<AutomationFlowRunRecord> RunManualTestTriggerAsync(
        Guid clientId,
        string triggerPayload,
        IEnumerable<Guid> matchedFlowIds,
        CancellationToken cancellationToken = default)
    {
        DateTime completedUtc = DateTime.UtcNow;

        List<Guid> flowIds = matchedFlowIds.ToList();

        AutomationFlowRunRecord run = new()
        {
            RunId = Guid.NewGuid(),
            ClientId = clientId,
            TriggerPayload = triggerPayload,
            StartedUtc = completedUtc,
            CompletedUtc = completedUtc,
            IsSuccess = true,
            MatchedFlowIds = flowIds,
            Steps =
            [
                new AutomationFlowRunStepRecord
                {
                    StepId = Guid.NewGuid(),
                    Name = "ManualTestTrigger",
                    IsSuccess = true,
                    ResultText = $"Manual trigger queued for {flowIds.Count} matched flow(s).",
                    CompletedUtc = completedUtc
                }
            ]
        };

        await RecordRunAsync(run);

        return run;
    }
}
