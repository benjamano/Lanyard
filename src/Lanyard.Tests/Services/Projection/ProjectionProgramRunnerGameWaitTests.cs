using Lanyard.Application.Services;
using Lanyard.Application.Services.Projection;
using Lanyard.Application.Services.Projection.Bindings;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Projection;

/// <summary>
/// "Wait for Game" steps in the runner. Every program here is: 0 Show Text, 1 Wait for Game,
/// 2 Show Text, played once, so "moved on" means the run reached step 2.
/// </summary>
[TestClass]
public class ProjectionProgramRunnerGameWaitTests
{
    // Each "minute" of Give Up After lasts this long here, so timeouts finish in a test.
    private static readonly TimeSpan TimeoutUnit = TimeSpan.FromMilliseconds(400);

    private sealed record Seeded(Guid ProjectorId, Guid LaserKioskId, Guid ProgramId);

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static ProjectionProgramRunnerService CreateRunner(DbContextOptions<ApplicationDbContext> options, ILaserGameStatusStore store)
    {
        Mock<ISystemDbContextFactory> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new ProjectionProgramRunnerService(
            factoryMock.Object,
            new ProjectionBindingResolver([], Mock.Of<ILogger<ProjectionBindingResolver>>()),
            Mock.Of<ILogger<ProjectionProgramRunnerService>>(),
            store)
        {
            GameWaitTimeoutUnit = TimeoutUnit
        };
    }

    private static async Task<Seeded> SeedAsync(
        DbContextOptions<ApplicationDbContext> options,
        ProjectionGameEvent gameEvent,
        int? giveUpAfter = null,
        int? secondsLeft = null,
        int laserKioskCompanyId = 0,
        bool laserKioskExists = true)
    {
        Guid projectorId = Guid.NewGuid();
        Guid laserKioskId = Guid.NewGuid();
        Guid programId = Guid.NewGuid();
        Guid showTextId = Guid.NewGuid();
        Guid waitId = Guid.NewGuid();

        await using ApplicationDbContext context = new(options);

        context.Clients.Add(new Client { Id = projectorId, CompanyId = 0, Name = "Projector" });

        if (laserKioskExists)
        {
            context.Clients.Add(new Client { Id = laserKioskId, CompanyId = laserKioskCompanyId, Name = "Arena 1" });
        }

        ProjectionProgramStepTemplateParameter Parameter(string name, string dataType) =>
            new() { Id = Guid.NewGuid(), TemplateId = waitId, Name = name, DataType = dataType, IsActive = true };

        ProjectionProgramStepTemplateParameter eventParameter = Parameter(GameWaitStepNames.GameEventParameter, GameWaitStepNames.GameEventDataType);
        ProjectionProgramStepTemplateParameter kioskParameter = Parameter(GameWaitStepNames.LaserKioskParameter, GameWaitStepNames.LaserKioskDataType);
        ProjectionProgramStepTemplateParameter secondsParameter = Parameter(GameWaitStepNames.SecondsLeftParameter, "Integer");
        ProjectionProgramStepTemplateParameter giveUpParameter = Parameter(GameWaitStepNames.GiveUpAfterParameter, "Integer");

        context.ProjectionProgramStepTemplates.AddRange(
            new ProjectionProgramStepTemplate { Id = showTextId, Name = "Show Text", IsActive = true },
            new ProjectionProgramStepTemplate
            {
                Id = waitId,
                Name = GameWaitStepNames.Template,
                IsActive = true,
                Parameters = [eventParameter, kioskParameter, secondsParameter, giveUpParameter]
            });

        context.ProjectionPrograms.Add(new ProjectionProgram { Id = programId, Name = "Arena loop", IsActive = true });

        Guid waitStepId = Guid.NewGuid();

        context.ProjectionProgramSteps.AddRange(
            new ProjectionProgramStep { Id = Guid.NewGuid(), ProjectionProgramId = programId, TemplateId = showTextId, SortOrder = 0, HoldForMilliseconds = 200, IsActive = true },
            new ProjectionProgramStep
            {
                Id = waitStepId,
                ProjectionProgramId = programId,
                TemplateId = waitId,
                SortOrder = 1,
                IsActive = true,
                ParameterValues =
                [
                    new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = waitStepId, ParameterId = eventParameter.Id, Value = gameEvent.ToString() },
                    new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = waitStepId, ParameterId = kioskParameter.Id, Value = laserKioskId.ToString() },
                    new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = waitStepId, ParameterId = secondsParameter.Id, Value = secondsLeft?.ToString() },
                    new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = waitStepId, ParameterId = giveUpParameter.Id, Value = giveUpAfter?.ToString() }
                ]
            },
            new ProjectionProgramStep { Id = Guid.NewGuid(), ProjectionProgramId = programId, TemplateId = showTextId, SortOrder = 2, HoldForMilliseconds = 60000, IsActive = true });

        await context.SaveChangesAsync();

        return new Seeded(projectorId, laserKioskId, programId);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static int? StepIndex(ProjectionProgramRunnerService runner, Seeded seed) =>
        runner.GetRunningState(seed.ProjectorId, 0)?.CurrentStepIndex;

    private static async Task StartAndReachTheWaitAsync(ProjectionProgramRunnerService runner, Seeded seed)
    {
        Result<Guid> started = await runner.StartAsync(seed.ProjectorId, 0, seed.ProgramId, false, 0, false);

        Assert.IsTrue(started.IsSuccess, started.Error);
        Assert.IsTrue(await WaitUntilAsync(() => StepIndex(runner, seed) == 1), "The run never reached the wait step.");
    }

    [TestMethod]
    public async Task WaitStep_HoldsUntilTheGameEnds_ThenMovesOn()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options, ProjectionGameEvent.GameEnds);
        LaserGameStatusStore store = new();
        store.UpdateStatus(seed.LaserKioskId, new LaserGameStatusDTO { Status = GameStatus.InGame, TimeRemainingSeconds = 300 });
        ProjectionProgramRunnerService runner = CreateRunner(options, store);

        await StartAndReachTheWaitAsync(runner, seed);

        // Well past a fixed step's default hold: the game is still on, so it keeps waiting.
        await Task.Delay(800);
        Assert.AreEqual(1, StepIndex(runner, seed));

        store.UpdateStatus(seed.LaserKioskId, new LaserGameStatusDTO { Status = GameStatus.NotStarted });

        Assert.IsTrue(await WaitUntilAsync(() => StepIndex(runner, seed) == 2), "The step didn't move on when the game ended.");
        runner.Stop(seed.ProjectorId, 0);
    }

    [TestMethod]
    public async Task WaitStep_SeesAShortGetReady_BetweenTwoTicks()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options, ProjectionGameEvent.GetReady);
        LaserGameStatusStore store = new();
        store.UpdateStatus(seed.LaserKioskId, new LaserGameStatusDTO { Status = GameStatus.NotStarted });
        ProjectionProgramRunnerService runner = CreateRunner(options, store);

        await StartAndReachTheWaitAsync(runner, seed);
        await Task.Delay(300);

        // Get ready and back within one tick: only the event sees it.
        store.UpdateStatus(seed.LaserKioskId, new LaserGameStatusDTO { Status = GameStatus.GetReady });
        store.UpdateStatus(seed.LaserKioskId, new LaserGameStatusDTO { Status = GameStatus.NotStarted });

        Assert.IsTrue(await WaitUntilAsync(() => StepIndex(runner, seed) == 2));
        runner.Stop(seed.ProjectorId, 0);
    }

    [TestMethod]
    public async Task WaitStep_IgnoresOtherKiosks()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options, ProjectionGameEvent.GameStarts);
        LaserGameStatusStore store = new();
        store.UpdateStatus(seed.LaserKioskId, new LaserGameStatusDTO { Status = GameStatus.NotStarted });
        ProjectionProgramRunnerService runner = CreateRunner(options, store);

        await StartAndReachTheWaitAsync(runner, seed);

        store.UpdateStatus(Guid.NewGuid(), new LaserGameStatusDTO { Status = GameStatus.InGame });
        await Task.Delay(600);

        Assert.AreEqual(1, StepIndex(runner, seed));
        runner.Stop(seed.ProjectorId, 0);
    }

    [TestMethod]
    public async Task WaitStep_GivesUpAfterItsTimeout_WhenTheKioskIsSilent()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        // 2 "minutes" = 800 ms here; no status at all for the kiosk.
        Seeded seed = await SeedAsync(options, ProjectionGameEvent.GameStarts, giveUpAfter: 2);
        ProjectionProgramRunnerService runner = CreateRunner(options, new LaserGameStatusStore());

        await StartAndReachTheWaitAsync(runner, seed);
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();

        Assert.IsTrue(await WaitUntilAsync(() => StepIndex(runner, seed) == 2), "The step never gave up.");
        Assert.IsTrue(waited.ElapsedMilliseconds >= 700, $"Gave up after {waited.ElapsedMilliseconds} ms; the timeout is 800 ms.");
        runner.Stop(seed.ProjectorId, 0);
    }

    [TestMethod]
    public async Task WaitStep_Timeout_DoesNotCountPausedTime()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options, ProjectionGameEvent.GameStarts, giveUpAfter: 2);
        ProjectionProgramRunnerService runner = CreateRunner(options, new LaserGameStatusStore());

        await StartAndReachTheWaitAsync(runner, seed);
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();

        runner.Pause(seed.ProjectorId, 0);
        await Task.Delay(1000);
        Assert.AreEqual(1, StepIndex(runner, seed), "A paused wait mustn't time out.");
        runner.Resume(seed.ProjectorId, 0);

        Assert.IsTrue(await WaitUntilAsync(() => StepIndex(runner, seed) == 2));
        Assert.IsTrue(waited.ElapsedMilliseconds >= 1700, $"Moved on after {waited.ElapsedMilliseconds} ms; expected the 800 ms timeout plus the 1000 ms pause.");
        runner.Stop(seed.ProjectorId, 0);
    }

    [TestMethod]
    public async Task WaitStep_EventWhilePaused_MovesOnOnlyOnResume()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options, ProjectionGameEvent.GameStarts);
        LaserGameStatusStore store = new();
        store.UpdateStatus(seed.LaserKioskId, new LaserGameStatusDTO { Status = GameStatus.NotStarted });
        ProjectionProgramRunnerService runner = CreateRunner(options, store);

        await StartAndReachTheWaitAsync(runner, seed);

        runner.Pause(seed.ProjectorId, 0);
        store.UpdateStatus(seed.LaserKioskId, new LaserGameStatusDTO { Status = GameStatus.InGame });

        // Gone again before resume: the event was still seen.
        store.UpdateStatus(seed.LaserKioskId, new LaserGameStatusDTO { Status = GameStatus.NotStarted });
        await Task.Delay(500);
        Assert.AreEqual(1, StepIndex(runner, seed), "A paused step stays on screen.");

        runner.Resume(seed.ProjectorId, 0);

        Assert.IsTrue(await WaitUntilAsync(() => StepIndex(runner, seed) == 2));
        runner.Stop(seed.ProjectorId, 0);
    }

    [TestMethod]
    public async Task WaitStep_SkipMovesOn_AndStopsListening()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options, ProjectionGameEvent.GameEnds);
        CountingStatusStore store = new();
        store.UpdateStatus(seed.LaserKioskId, new LaserGameStatusDTO { Status = GameStatus.InGame, TimeRemainingSeconds = 300 });
        ProjectionProgramRunnerService runner = CreateRunner(options, store);

        await StartAndReachTheWaitAsync(runner, seed);
        Assert.IsTrue(await WaitUntilAsync(() => store.SubscriberCount == 1), "The wait step should listen for publishes.");

        Assert.IsTrue(runner.SkipToNextStep(seed.ProjectorId, 0).IsSuccess);

        Assert.IsTrue(await WaitUntilAsync(() => StepIndex(runner, seed) == 2));
        Assert.IsTrue(await WaitUntilAsync(() => store.SubscriberCount == 0), "Leaving the step must unsubscribe.");
        runner.Stop(seed.ProjectorId, 0);
    }

    [TestMethod]
    public async Task WaitStep_StopDuringTheWait_EndsTheRun_AndStopsListening()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options, ProjectionGameEvent.GameEnds);
        CountingStatusStore store = new();
        store.UpdateStatus(seed.LaserKioskId, new LaserGameStatusDTO { Status = GameStatus.InGame, TimeRemainingSeconds = 300 });
        ProjectionProgramRunnerService runner = CreateRunner(options, store);

        await StartAndReachTheWaitAsync(runner, seed);

        runner.Stop(seed.ProjectorId, 0);

        Assert.IsTrue(await WaitUntilAsync(() => runner.GetRunningState(seed.ProjectorId, 0) == null));
        Assert.IsTrue(await WaitUntilAsync(() => store.SubscriberCount == 0));
    }

    [TestMethod]
    public async Task RunningState_DescribesTheWait()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options, ProjectionGameEvent.TimeLeftBelow, giveUpAfter: 30, secondsLeft: 60);
        ProjectionProgramRunnerService runner = CreateRunner(options, new LaserGameStatusStore());

        Result<Guid> started = await runner.StartAsync(seed.ProjectorId, 0, seed.ProgramId, false, 0, false);
        Assert.IsTrue(started.IsSuccess, started.Error);

        Assert.IsNull(runner.GetRunningState(seed.ProjectorId, 0)!.GameWait, "A Show Text step isn't waiting for anything.");
        Assert.IsTrue(await WaitUntilAsync(() => StepIndex(runner, seed) == 1));

        ProjectionGameWaitState? wait = runner.GetRunningState(seed.ProjectorId, 0)!.GameWait;
        Assert.AreEqual(new ProjectionGameWaitState(ProjectionGameEvent.TimeLeftBelow, seed.LaserKioskId, "Arena 1", 60, 30), wait);

        runner.Stop(seed.ProjectorId, 0);
    }

    [TestMethod]
    public async Task StartAsync_RefusesALaserKioskFromAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options, ProjectionGameEvent.GameEnds, laserKioskCompanyId: 99);
        ProjectionProgramRunnerService runner = CreateRunner(options, new LaserGameStatusStore());

        Result<Guid> started = await runner.StartAsync(seed.ProjectorId, 0, seed.ProgramId, false, 0, false);

        Assert.IsFalse(started.IsSuccess);
        StringAssert.Contains(started.Error, "Step 2");
        Assert.IsNull(runner.GetRunningState(seed.ProjectorId, 0));
    }

    [TestMethod]
    public async Task StartAsync_RefusesALaserKioskThatNoLongerExists()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Seeded seed = await SeedAsync(options, ProjectionGameEvent.GameEnds, laserKioskExists: false);
        ProjectionProgramRunnerService runner = CreateRunner(options, new LaserGameStatusStore());

        Result<Guid> started = await runner.StartAsync(seed.ProjectorId, 0, seed.ProgramId, false, 0, false);

        Assert.IsFalse(started.IsSuccess);
        Assert.IsNull(runner.GetRunningState(seed.ProjectorId, 0));
    }

    // Counts subscribers, so a test can tell a finished wait unsubscribed.
    private sealed class CountingStatusStore : ILaserGameStatusStore
    {
        private readonly LaserGameStatusStore _inner = new();
        private readonly List<Action<LaserGameStatusDTO>> _handlers = [];

        public int SubscriberCount
        {
            get
            {
                lock (_handlers)
                {
                    return _handlers.Count;
                }
            }
        }

        public event Action<LaserGameStatusDTO>? OnStatusUpdated
        {
            add { lock (_handlers) { _handlers.Add(value!); } }
            remove { lock (_handlers) { _handlers.Remove(value!); } }
        }

        public void UpdateStatus(Guid clientId, LaserGameStatusDTO status)
        {
            _inner.UpdateStatus(clientId, status);

            Action<LaserGameStatusDTO>[] handlers;

            lock (_handlers)
            {
                handlers = [.. _handlers];
            }

            foreach (Action<LaserGameStatusDTO> handler in handlers)
            {
                handler(status);
            }
        }

        public bool TryGetStatus(Guid clientId, out LaserGameStatusDTO? status) => _inner.TryGetStatus(clientId, out status);

        public IReadOnlyDictionary<Guid, LaserGameStatusDTO> GetAllStatuses() => _inner.GetAllStatuses();

        public void RemoveStatus(Guid clientId) => _inner.RemoveStatus(clientId);
    }
}
