using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dmx;

// Start/stop/restart ordering in DmxSceneRunnerService: the races behind stuck flash scenes and
// blacked-out restarts.
[TestClass]
public class DmxSceneRunnerLifecycleTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static Mock<ISystemDbContextFactory> GetFactoryMock(DbContextOptions<ApplicationDbContext> options, Task? gate = null)
    {
        Mock<ISystemDbContextFactory> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken _) =>
            {
                if (gate != null)
                {
                    await gate;
                }

                return new ApplicationDbContext(options);
            });

        return factoryMock;
    }

    private static async Task<(Guid clientId, Guid sceneId)> SeedSceneAsync(
        DbContextOptions<ApplicationDbContext> options, bool isMomentary, bool loop, TimeSpan stepDuration)
    {
        Guid clientId = Guid.NewGuid();
        Guid sceneId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();

        await using ApplicationDbContext context = new(options);

        context.DmxScenes.Add(new DmxScene
        {
            Id = sceneId,
            ClientId = clientId,
            Name = "Scene",
            IsActive = true,
            Loop = loop,
            IsMomentary = isMomentary,
            CreateByUserId = "test-user"
        });

        DmxSceneStep step = new()
        {
            Id = stepId,
            SceneId = sceneId,
            StepNumber = 1,
            Name = "Step 1",
            Duration = stepDuration,
            CreateByUserId = "test-user"
        };

        step.ChannelValues.Add(new DmxSceneStepChannelValue { Id = Guid.NewGuid(), SceneStepId = stepId, ChannelNumber = 1, Value = 255, CreateByUserId = "test-user" });

        context.DmxSceneSteps.Add(step);
        await context.SaveChangesAsync();

        return (clientId, sceneId);
    }

    private static DmxSceneRunnerService GetRunner(Mock<ISystemDbContextFactory> factory, Mock<IDmxService> dmx) =>
        new(factory.Object, dmx.Object, Mock.Of<IBeatClockService>(), Mock.Of<ILogger<DmxSceneRunnerService>>());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private static bool IsResetBatch(IReadOnlyList<DmxChannel> batch) => batch.All(c => c.Value == 0);

    [TestMethod]
    public async Task StopScene_WhileTheStartIsStillLoading_CancelsIt()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid clientId, Guid sceneId) = await SeedSceneAsync(options, isMomentary: true, loop: true, TimeSpan.FromSeconds(5));

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IDmxService> dmx = new();
        DmxSceneRunnerService runner = GetRunner(GetFactoryMock(options, gate.Task), dmx);

        int started = 0;
        runner.OnSceneStarted += (_, _) => started++;

        // A quick tap: the release lands before the scene has loaded.
        Task<Result<bool>> start = runner.StartSceneAsync(clientId, sceneId);
        runner.StopScene(sceneId);
        gate.SetResult();

        Result<bool> result = await start;

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, started);
        Assert.AreEqual(0, runner.GetRunningSceneIds(clientId).Count);

        await Task.Delay(50);
        dmx.Verify(d => d.UpdateChannelValuesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DmxChannel>>()), Times.Never);
    }

    [TestMethod]
    public async Task StartSceneAsync_SceneFromAnotherKiosk_IsRefused()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (_, Guid sceneId) = await SeedSceneAsync(options, isMomentary: false, loop: false, TimeSpan.FromSeconds(1));

        Mock<IDmxService> dmx = new();
        DmxSceneRunnerService runner = GetRunner(GetFactoryMock(options), dmx);
        Guid otherKiosk = Guid.NewGuid();

        Result<bool> result = await runner.StartSceneAsync(otherKiosk, sceneId);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(0, runner.GetRunningSceneIds(otherKiosk).Count);
        dmx.Verify(d => d.UpdateChannelValuesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<DmxChannel>>()), Times.Never);
    }

    [TestMethod]
    public async Task Restart_OfARunningMomentaryScene_DoesNotBlackOutTheNewRun()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid clientId, Guid sceneId) = await SeedSceneAsync(options, isMomentary: true, loop: true, TimeSpan.FromSeconds(5));

        List<IReadOnlyList<DmxChannel>> sent = [];
        Mock<IDmxService> dmx = new();
        dmx.Setup(d => d.UpdateChannelValuesAsync(clientId, It.IsAny<IReadOnlyList<DmxChannel>>()))
            .Callback((Guid _, IReadOnlyList<DmxChannel> batch) => { lock (sent) { sent.Add(batch); } })
            .Returns(Task.CompletedTask);

        DmxSceneRunnerService runner = GetRunner(GetFactoryMock(options), dmx);

        int stopped = 0;
        runner.OnSceneStopped += (_, _) => Interlocked.Increment(ref stopped);

        await runner.StartSceneAsync(clientId, sceneId);
        await runner.StartSceneAsync(clientId, sceneId);
        await Task.Delay(150);

        lock (sent)
        {
            Assert.IsFalse(sent.Any(IsResetBatch), "The replaced run must not zero the channels the new run is driving.");
        }

        Assert.AreEqual(0, stopped, "A restart isn't a stop.");
        CollectionAssert.AreEqual(new[] { sceneId }, runner.GetRunningSceneIds(clientId));

        runner.StopScene(sceneId);
        await WaitUntilAsync(() => runner.GetRunningSceneIds(clientId).Count == 0 && Volatile.Read(ref stopped) == 1);

        lock (sent)
        {
            Assert.AreEqual(1, sent.Count(IsResetBatch));
        }

        Assert.AreEqual(1, stopped);
    }

    [TestMethod]
    public async Task ZeroLengthSteps_DoNotSpinTheLoop()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid clientId, Guid sceneId) = await SeedSceneAsync(options, isMomentary: false, loop: true, TimeSpan.Zero);

        int writes = 0;
        Mock<IDmxService> dmx = new();
        dmx.Setup(d => d.UpdateChannelValuesAsync(clientId, It.IsAny<IReadOnlyList<DmxChannel>>()))
            .Callback(() => Interlocked.Increment(ref writes))
            .Returns(Task.CompletedTask);

        DmxSceneRunnerService runner = GetRunner(GetFactoryMock(options), dmx);

        await runner.StartSceneAsync(clientId, sceneId);
        await Task.Delay(250);
        runner.StopScene(sceneId);
        await WaitUntilAsync(() => runner.GetRunningSceneIds(clientId).Count == 0);

        // 250 ms at the 25 ms minimum is about 10 steps; without it this was thousands.
        Assert.IsTrue(writes is > 0 and <= 20, $"{writes} step writes in 250 ms");
    }

    [TestMethod]
    public async Task PlaybackFault_RaisesFailedThenStopped()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid clientId, Guid sceneId) = await SeedSceneAsync(options, isMomentary: false, loop: true, TimeSpan.FromSeconds(1));

        Mock<IDmxService> dmx = new();
        dmx.Setup(d => d.UpdateChannelValuesAsync(clientId, It.IsAny<IReadOnlyList<DmxChannel>>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        DmxSceneRunnerService runner = GetRunner(GetFactoryMock(options), dmx);

        List<string> events = [];
        runner.OnSceneFailed += (_, _, reason) => { lock (events) { events.Add("failed: " + reason); } };
        runner.OnSceneStopped += (_, _) => { lock (events) { events.Add("stopped"); } };

        await runner.StartSceneAsync(clientId, sceneId);
        await WaitUntilAsync(() => { lock (events) { return events.Count == 2; } });

        lock (events)
        {
            Assert.AreEqual(2, events.Count);
            StringAssert.Contains(events[0], "hub down");
            Assert.AreEqual("stopped", events[1]);
        }
    }

    [TestMethod]
    public async Task ThrowingSubscriber_DoesNotStopTheSceneOrOtherSubscribers()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (Guid clientId, Guid sceneId) = await SeedSceneAsync(options, isMomentary: false, loop: true, TimeSpan.FromSeconds(5));

        Mock<IDmxService> dmx = new();
        DmxSceneRunnerService runner = GetRunner(GetFactoryMock(options), dmx);

        bool otherHeard = false;
        runner.OnSceneStarted += (_, _) => throw new InvalidOperationException("bad desk");
        runner.OnSceneStarted += (_, _) => otherHeard = true;

        Result<bool> result = await runner.StartSceneAsync(clientId, sceneId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(otherHeard);
        CollectionAssert.AreEqual(new[] { sceneId }, runner.GetRunningSceneIds(clientId));

        runner.StopScene(sceneId);
        await WaitUntilAsync(() => runner.GetRunningSceneIds(clientId).Count == 0);
    }
}
