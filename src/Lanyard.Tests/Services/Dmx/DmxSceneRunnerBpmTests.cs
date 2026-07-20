using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dmx;

[TestClass]
public class DmxSceneRunnerBpmTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static Mock<IDbContextFactory<ApplicationDbContext>> GetFactoryMock(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock;
    }

    private static async Task<(Guid clientId, Guid sceneId)> SeedSceneAsync(
        DbContextOptions<ApplicationDbContext> options, bool bpmSyncEnabled, TimeSpan stepDuration, double beats)
    {
        Guid clientId = Guid.NewGuid();
        Guid sceneId = Guid.NewGuid();

        await using ApplicationDbContext context = new(options);

        context.DmxScenes.Add(new DmxScene
        {
            Id = sceneId,
            ClientId = clientId,
            Name = "Test scene",
            IsActive = true,
            Loop = false,
            BpmSyncEnabled = bpmSyncEnabled,
            CreateByUserId = "test-user"
        });

        for (int stepNumber = 1; stepNumber <= 2; stepNumber++)
        {
            context.DmxSceneSteps.Add(new DmxSceneStep
            {
                Id = Guid.NewGuid(),
                SceneId = sceneId,
                StepNumber = stepNumber,
                Name = $"Step {stepNumber}",
                Duration = stepDuration,
                Beats = beats,
                CreateByUserId = "test-user"
            });
        }

        await context.SaveChangesAsync();

        return (clientId, sceneId);
    }

    private static async Task WaitForSceneToStopAsync(DmxSceneRunnerService runner, Guid clientId)
    {
        // The loop runs on a background task; poll briefly rather than asserting
        // exact timing, which would be flaky on a busy build machine.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);

        while (runner.GetRunningSceneIds(clientId).Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [TestMethod]
    public async Task RunScene_BpmSyncEnabled_UsesBeatClockDelay()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        // Long fixed Duration but a tiny beat delay: the scene only finishes fast
        // if the beat clock's delay is what actually drives the steps.
        (Guid clientId, Guid sceneId) = await SeedSceneAsync(options, bpmSyncEnabled: true, stepDuration: TimeSpan.FromMinutes(5), beats: 4);

        Mock<IBeatClockService> beatClockMock = new();
        beatClockMock.Setup(b => b.GetDelayUntilNextStepAsync(clientId, 4))
            .ReturnsAsync(TimeSpan.FromMilliseconds(20));

        DmxSceneRunnerService runner = new(
            GetFactoryMock(options).Object,
            Mock.Of<IDmxService>(),
            beatClockMock.Object,
            Mock.Of<ILogger<DmxSceneRunnerService>>());

        Result<bool> startResult = await runner.StartSceneAsync(clientId, sceneId);
        Assert.IsTrue(startResult.IsSuccess);

        await WaitForSceneToStopAsync(runner, clientId);

        Assert.AreEqual(0, runner.GetRunningSceneIds(clientId).Count, "Scene should have finished via beat-clock delays, not the 5-minute Duration");
        beatClockMock.Verify(b => b.GetDelayUntilNextStepAsync(clientId, 4), Times.Exactly(2));
    }

    [TestMethod]
    public async Task RunScene_BeatClockReturnsNull_FallsBackToStepDuration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        (Guid clientId, Guid sceneId) = await SeedSceneAsync(options, bpmSyncEnabled: true, stepDuration: TimeSpan.FromMilliseconds(20), beats: 1);

        Mock<IBeatClockService> beatClockMock = new();
        beatClockMock.Setup(b => b.GetDelayUntilNextStepAsync(It.IsAny<Guid>(), It.IsAny<double>()))
            .ReturnsAsync((TimeSpan?)null);

        DmxSceneRunnerService runner = new(
            GetFactoryMock(options).Object,
            Mock.Of<IDmxService>(),
            beatClockMock.Object,
            Mock.Of<ILogger<DmxSceneRunnerService>>());

        Result<bool> startResult = await runner.StartSceneAsync(clientId, sceneId);
        Assert.IsTrue(startResult.IsSuccess);

        await WaitForSceneToStopAsync(runner, clientId);

        Assert.AreEqual(0, runner.GetRunningSceneIds(clientId).Count, "Scene should have finished using the Duration fallback");
        beatClockMock.Verify(b => b.GetDelayUntilNextStepAsync(clientId, 1), Times.Exactly(2));
    }

    [TestMethod]
    public async Task RunScene_BpmSyncDisabled_NeverConsultsBeatClock()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        (Guid clientId, Guid sceneId) = await SeedSceneAsync(options, bpmSyncEnabled: false, stepDuration: TimeSpan.FromMilliseconds(20), beats: 1);

        Mock<IBeatClockService> beatClockMock = new();

        DmxSceneRunnerService runner = new(
            GetFactoryMock(options).Object,
            Mock.Of<IDmxService>(),
            beatClockMock.Object,
            Mock.Of<ILogger<DmxSceneRunnerService>>());

        Result<bool> startResult = await runner.StartSceneAsync(clientId, sceneId);
        Assert.IsTrue(startResult.IsSuccess);

        await WaitForSceneToStopAsync(runner, clientId);

        beatClockMock.Verify(b => b.GetDelayUntilNextStepAsync(It.IsAny<Guid>(), It.IsAny<double>()), Times.Never());
    }
}
