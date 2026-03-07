using Lanyard.Application.Services.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Automation;

[TestClass]
public class AutomationFlowRunLogServiceTests
{
    [TestMethod]
    public async Task RecordRunAsync_ShouldStoreAndReturnRecentRun()
    {
        AutomationFlowRunLogService service = new();
        Guid flowId = Guid.NewGuid();

        AutomationFlowRunRecord runRecord = new()
        {
            RunId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            TriggerPayload = "test-payload",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
            IsSuccess = true,
            MatchedFlowIds = [flowId],
            Steps =
            [
                new AutomationFlowRunStepRecord
                {
                    StepId = Guid.NewGuid(),
                    Name = "Resolve",
                    IsSuccess = true,
                    ResultText = "ok",
                    CompletedUtc = DateTime.UtcNow
                }
            ]
        };

        await service.RecordRunAsync(runRecord);

        IReadOnlyList<AutomationFlowRunRecord> recentRuns = service.GetRecentRuns();

        Assert.AreEqual(1, recentRuns.Count);
        Assert.AreEqual("test-payload", recentRuns[0].TriggerPayload);
    }

    [TestMethod]
    public async Task GetLatestStatusByFlow_ShouldUseMostRecentRunPerFlow()
    {
        AutomationFlowRunLogService service = new();
        Guid flowId = Guid.NewGuid();

        await service.RecordRunAsync(new AutomationFlowRunRecord
        {
            RunId = Guid.NewGuid(),
            TriggerPayload = "old",
            StartedUtc = DateTime.UtcNow.AddMinutes(-2),
            CompletedUtc = DateTime.UtcNow.AddMinutes(-2),
            IsSuccess = true,
            MatchedFlowIds = [flowId]
        });

        await service.RecordRunAsync(new AutomationFlowRunRecord
        {
            RunId = Guid.NewGuid(),
            TriggerPayload = "new",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
            IsSuccess = false,
            ErrorText = "boom",
            MatchedFlowIds = [flowId]
        });

        IReadOnlyList<AutomationFlowLatestStatus> statuses = service.GetLatestStatusByFlow();

        Assert.AreEqual(1, statuses.Count);
        Assert.AreEqual("new", statuses[0].TriggerPayload);
        Assert.IsFalse(statuses[0].IsSuccess);
        Assert.AreEqual("boom", statuses[0].LatestError);
    }
}
