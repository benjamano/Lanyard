using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Projection;

[TestClass]
public class ProjectionProgramRunnerServiceTests
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

    private static ProjectionProgramRunnerService CreateRunner(DbContextOptions<ApplicationDbContext> options)
    {
        return new ProjectionProgramRunnerService(
            GetFactoryMock(options).Object,
            Mock.Of<ILogger<ProjectionProgramRunnerService>>());
    }

    private static async Task<Guid> SeedProgramAsync(
        DbContextOptions<ApplicationDbContext> options,
        int stepCount,
        int holdForMilliseconds,
        bool stepsActive = true,
        bool programActive = true)
    {
        Guid programId = Guid.NewGuid();
        Guid templateId = Guid.NewGuid();

        await using ApplicationDbContext context = new(options);

        context.ProjectionProgramStepTemplates.Add(new ProjectionProgramStepTemplate
        {
            Id = templateId,
            Name = "Show Text",
            IsActive = true
        });

        context.ProjectionPrograms.Add(new ProjectionProgram
        {
            Id = programId,
            Name = "Test program",
            IsActive = programActive
        });

        for (int i = 0; i < stepCount; i++)
        {
            context.ProjectionProgramSteps.Add(new ProjectionProgramStep
            {
                Id = Guid.NewGuid(),
                ProjectionProgramId = programId,
                TemplateId = templateId,
                SortOrder = i,
                HoldForMilliseconds = holdForMilliseconds,
                IsActive = stepsActive
            });
        }

        await context.SaveChangesAsync();

        return programId;
    }

    // The run loop lives on a background task and re-checks state every 200ms, so every
    // assertion about playback polls rather than asserting exact timing - the same reason
    // the DMX runner's tests poll instead of sleeping for a fixed duration.
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

    [TestMethod]
    public async Task StartAsync_ReturnsFail_WhenProgramDoesNotExist()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        ProjectionProgramRunnerService runner = CreateRunner(options);

        Result<Guid> result = await runner.StartAsync(Guid.NewGuid(), 0, Guid.NewGuid(), true, 0, false);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Projection program not found.", result.Error);
    }

    [TestMethod]
    public async Task StartAsync_ReturnsFail_WhenProgramIsInactive()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid programId = await SeedProgramAsync(options, stepCount: 2, holdForMilliseconds: 200, programActive: false);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        Result<Guid> result = await runner.StartAsync(Guid.NewGuid(), 0, programId, true, 0, false);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task StartAsync_ReturnsFail_WhenProgramHasNoActiveSteps()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid programId = await SeedProgramAsync(options, stepCount: 2, holdForMilliseconds: 200, stepsActive: false);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        Result<Guid> result = await runner.StartAsync(Guid.NewGuid(), 0, programId, true, 0, false);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Projection program has no playable steps.", result.Error);
    }

    [TestMethod]
    public async Task StartAsync_RaisesStartedEvent_AndExposesRunningState()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 3, holdForMilliseconds: 1000);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        List<(Guid ClientId, int DisplayIndex, Guid ProgramId)> started = [];
        runner.OnProgramStarted += (c, d, p) => started.Add((c, d, p));

        Result<Guid> result = await runner.StartAsync(clientId, 1, programId, true, 0, false);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(new[] { (clientId, 1, programId) }, started);

        ProjectionRunState? state = runner.GetRunningState(clientId, 1);

        Assert.IsNotNull(state);
        Assert.AreEqual(programId, state.ProgramId);
        Assert.HasCount(3, state.Steps);
        Assert.AreEqual(1, state.DisplayIndex);
        Assert.IsFalse(state.IsPaused);
        Assert.AreEqual(result.Data, state.RunId);

        runner.Stop(clientId, 1);
    }

    [TestMethod]
    public async Task RunLoop_AdvancesThroughStepsInSortOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 3, holdForMilliseconds: 200);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        List<int> advancedTo = [];
        runner.OnProgramStepAdvanced += (c, d, index) => advancedTo.Add(index);

        await runner.StartAsync(clientId, 0, programId, false, 0, false);

        await WaitUntilAsync(() => runner.GetRunningState(clientId, 0) is null);

        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, advancedTo);
    }

    [TestMethod]
    public async Task StartAsync_ReplacesAnExistingRunOnTheSameDisplay()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid firstProgramId = await SeedProgramAsync(options, stepCount: 2, holdForMilliseconds: 2000);
        Guid secondProgramId = await SeedProgramAsync(options, stepCount: 2, holdForMilliseconds: 2000);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        Result<Guid> firstResult = await runner.StartAsync(clientId, 0, firstProgramId, true, 0, false);
        Result<Guid> secondResult = await runner.StartAsync(clientId, 0, secondProgramId, true, 0, false);

        Assert.IsTrue(secondResult.IsSuccess);

        ProjectionRunState? state = runner.GetRunningState(clientId, 0);

        Assert.IsNotNull(state);
        Assert.AreEqual(secondProgramId, state.ProgramId);
        Assert.AreEqual(secondResult.Data, state.RunId);
        Assert.AreNotEqual(firstResult.Data, state.RunId);

        // The replaced run must not remove the replacing run's entry when it tears down.
        Assert.IsTrue(await WaitUntilAsync(() => runner.GetRunningState(clientId, 0)?.RunId == secondResult.Data));

        runner.Stop(clientId, 0);
    }

    [TestMethod]
    public async Task StartAsync_RunsDisplaysOfTheSameClientIndependently()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid firstProgramId = await SeedProgramAsync(options, stepCount: 2, holdForMilliseconds: 2000);
        Guid secondProgramId = await SeedProgramAsync(options, stepCount: 2, holdForMilliseconds: 2000);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        await runner.StartAsync(clientId, 0, firstProgramId, true, 0, false);
        await runner.StartAsync(clientId, 1, secondProgramId, true, 0, false);

        Assert.AreEqual(firstProgramId, runner.GetRunningState(clientId, 0)?.ProgramId);
        Assert.AreEqual(secondProgramId, runner.GetRunningState(clientId, 1)?.ProgramId);
        Assert.HasCount(2, runner.GetRunningStates());

        runner.Stop(clientId, 0);

        Assert.IsTrue(await WaitUntilAsync(() => runner.GetRunningState(clientId, 0) is null));
        Assert.IsNotNull(runner.GetRunningState(clientId, 1));

        runner.Stop(clientId, 1);
    }

    [TestMethod]
    public async Task Pause_HoldsTheCurrentStep_AndResumeContinues()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 3, holdForMilliseconds: 400);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        List<bool> pauseEvents = [];
        runner.OnProgramPauseChanged += (c, d, paused) => pauseEvents.Add(paused);

        await runner.StartAsync(clientId, 0, programId, true, 0, false);

        Result<bool> pauseResult = runner.Pause(clientId, 0);
        Assert.IsTrue(pauseResult.IsSuccess);

        int pausedAtIndex = runner.GetRunningState(clientId, 0)!.CurrentStepIndex;

        // Comfortably longer than a step's hold: a paused run must not advance at all.
        await Task.Delay(1200);

        Assert.IsTrue(runner.GetRunningState(clientId, 0)!.IsPaused);
        Assert.AreEqual(pausedAtIndex, runner.GetRunningState(clientId, 0)!.CurrentStepIndex);

        Result<bool> resumeResult = runner.Resume(clientId, 0);
        Assert.IsTrue(resumeResult.IsSuccess);

        Assert.IsTrue(await WaitUntilAsync(() => runner.GetRunningState(clientId, 0)?.CurrentStepIndex != pausedAtIndex));

        CollectionAssert.AreEqual(new[] { true, false }, pauseEvents);

        runner.Stop(clientId, 0);
    }

    [TestMethod]
    public void Pause_ReturnsFail_WhenNothingIsRunningOnThatDisplay()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        ProjectionProgramRunnerService runner = CreateRunner(options);

        Result<bool> result = runner.Pause(Guid.NewGuid(), 0);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("No projection program is running on that display.", result.Error);
    }

    [TestMethod]
    public async Task SkipToStep_JumpsPlaybackToThatStep()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 4, holdForMilliseconds: 5000);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        await runner.StartAsync(clientId, 0, programId, true, 0, false);

        Result<bool> skipResult = runner.SkipToStep(clientId, 0, 3);
        Assert.IsTrue(skipResult.IsSuccess);

        Assert.IsTrue(await WaitUntilAsync(() => runner.GetRunningState(clientId, 0)?.CurrentStepIndex == 3));

        runner.Stop(clientId, 0);
    }

    [TestMethod]
    public async Task SkipToStep_ReturnsFail_WhenIndexIsOutsideTheProgram()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 2, holdForMilliseconds: 5000);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        await runner.StartAsync(clientId, 0, programId, true, 0, false);

        Result<bool> skipResult = runner.SkipToStep(clientId, 0, 7);

        Assert.IsFalse(skipResult.IsSuccess);
        Assert.AreEqual("Step index is outside the running program.", skipResult.Error);

        runner.Stop(clientId, 0);
    }

    [TestMethod]
    public async Task SkipToPreviousStep_WrapsBackwardsFromTheFirstStep()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 3, holdForMilliseconds: 5000);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        await runner.StartAsync(clientId, 0, programId, true, 0, false);

        Assert.IsTrue(runner.SkipToPreviousStep(clientId, 0).IsSuccess);

        Assert.IsTrue(await WaitUntilAsync(() => runner.GetRunningState(clientId, 0)?.CurrentStepIndex == 2));

        runner.Stop(clientId, 0);
    }

    [TestMethod]
    public async Task Stop_WithAnotherRunsId_DoesNotStopTheCurrentRun()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 2, holdForMilliseconds: 5000);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        Result<Guid> startResult = await runner.StartAsync(clientId, 0, programId, true, 0, false);

        Assert.IsTrue(runner.Stop(clientId, 0, Guid.NewGuid()).IsSuccess);

        await Task.Delay(300);

        Assert.IsNotNull(runner.GetRunningState(clientId, 0));

        Assert.IsTrue(runner.Stop(clientId, 0, startResult.Data).IsSuccess);
        Assert.IsTrue(await WaitUntilAsync(() => runner.GetRunningState(clientId, 0) is null));
    }

    [TestMethod]
    public async Task NonRepeatingRun_PlaysOnce_ThenStops()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 2, holdForMilliseconds: 200);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        List<int> advancedTo = [];
        runner.OnProgramStepAdvanced += (c, d, index) => advancedTo.Add(index);

        await runner.StartAsync(clientId, 0, programId, repeatInfinitely: false, repeatCount: 0, isTemporaryTrigger: false);

        Assert.IsTrue(await WaitUntilAsync(() => runner.GetRunningState(clientId, 0) is null));

        CollectionAssert.AreEqual(new[] { 0, 1 }, advancedTo);
    }

    [TestMethod]
    public async Task RepeatCount_PlaysThatManyExtraPasses()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 2, holdForMilliseconds: 200);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        List<int> advancedTo = [];
        runner.OnProgramStepAdvanced += (c, d, index) => advancedTo.Add(index);

        await runner.StartAsync(clientId, 0, programId, repeatInfinitely: false, repeatCount: 2, isTemporaryTrigger: false);

        Assert.IsTrue(await WaitUntilAsync(() => runner.GetRunningState(clientId, 0) is null, 10000));

        CollectionAssert.AreEqual(new[] { 0, 1, 0, 1, 0, 1 }, advancedTo);
    }

    [TestMethod]
    public async Task TemporaryTrigger_RaisesCompletedNaturally_WhenItFinishesOnItsOwn()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 1, holdForMilliseconds: 200);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        List<(Guid ClientId, int DisplayIndex, Guid ProgramId)> completed = [];
        List<Guid> stopped = [];

        runner.OnProgramCompletedNaturally += (c, d, p) => completed.Add((c, d, p));
        runner.OnProgramStopped += (c, d, p) => stopped.Add(p);

        await runner.StartAsync(clientId, 2, programId, repeatInfinitely: false, repeatCount: 0, isTemporaryTrigger: true);

        Assert.IsTrue(await WaitUntilAsync(() => completed.Count > 0));

        CollectionAssert.AreEqual(new[] { (clientId, 2, programId) }, completed);
        CollectionAssert.AreEqual(new[] { programId }, stopped);
    }

    [TestMethod]
    public async Task TemporaryTrigger_DoesNotRaiseCompletedNaturally_WhenStoppedManually()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 2, holdForMilliseconds: 5000);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        int completedCount = 0;
        int stoppedCount = 0;

        runner.OnProgramCompletedNaturally += (c, d, p) => completedCount++;
        runner.OnProgramStopped += (c, d, p) => stoppedCount++;

        await runner.StartAsync(clientId, 0, programId, repeatInfinitely: false, repeatCount: 0, isTemporaryTrigger: true);

        runner.Stop(clientId, 0);

        Assert.IsTrue(await WaitUntilAsync(() => stoppedCount > 0));

        Assert.AreEqual(0, completedCount);
    }

    [TestMethod]
    public async Task AmbientRun_DoesNotRaiseCompletedNaturally_WhenItFinishes()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();
        Guid programId = await SeedProgramAsync(options, stepCount: 1, holdForMilliseconds: 200);

        ProjectionProgramRunnerService runner = CreateRunner(options);

        int completedCount = 0;
        int stoppedCount = 0;

        runner.OnProgramCompletedNaturally += (c, d, p) => completedCount++;
        runner.OnProgramStopped += (c, d, p) => stoppedCount++;

        await runner.StartAsync(clientId, 0, programId, repeatInfinitely: false, repeatCount: 0, isTemporaryTrigger: false);

        Assert.IsTrue(await WaitUntilAsync(() => stoppedCount > 0));

        Assert.AreEqual(0, completedCount);
    }
}
