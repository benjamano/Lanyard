#nullable enable

using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

/// <summary>
/// "Only if" conditions on each trigger type, end to end through the engine: what runs, what is
/// skipped, and what the execution log records. The condition logic itself is covered in
/// AutomationConditionEvaluatorTests.
/// </summary>
[TestClass]
public class AutomationEngineServiceConditionTests
{
    private const string EnabledSettingKey = "AutomationEngine.Enabled";

    // 15:00 UTC on Wednesday 15 July 2026 is 16:00 venue time (BST).
    private static readonly DateTime SummerAfternoonUtc = new(2026, 7, 15, 15, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        DbContextOptions<ApplicationDbContext> Options,
        AutomationEngineService Engine,
        RecordingActionExecutor Executor,
        LaserGameStatusStore Store,
        TestClock Clock);

    private static async Task<Harness> CreateHarnessAsync()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        Mock<ISystemDbContextFactory> factoryMock = new();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.AppSettings.Add(new AppSetting
            {
                Id = Guid.NewGuid(),
                Key = EnabledSettingKey,
                Value = "true",
                CreateDate = DateTime.UtcNow
            });

            await ctx.SaveChangesAsync();
        }

        RecordingActionExecutor executor = new();
        LaserGameStatusStore store = new();
        TestClock clock = new(SummerAfternoonUtc);

        AutomationEngineService engine = new(
            factoryMock.Object,
            [executor],
            store,
            clock,
            NullLogger<AutomationEngineService>.Instance);

        return new Harness(options, engine, executor, store, clock);
    }

    private static AutomationRuleCondition TimeWindow(TimeOnly start, TimeOnly end)
    {
        return new AutomationRuleCondition
        {
            Id = Guid.NewGuid(),
            ConditionType = AutomationConditionType.TimeWindow,
            ParametersJson = AutomationConditionParameters.Serialize(new TimeWindowConditionParameters(start, end)),
            IsActive = true
        };
    }

    private static AutomationRuleCondition KioskInGame(Guid clientId, bool inGame, bool isActive = true)
    {
        return new AutomationRuleCondition
        {
            Id = Guid.NewGuid(),
            ConditionType = AutomationConditionType.ClientGameStatus,
            ParametersJson = AutomationConditionParameters.Serialize(new ClientGameStatusConditionParameters(clientId, inGame)),
            IsActive = isActive
        };
    }

    private static async Task<Guid> SeedKioskAsync(Harness harness, string name, int companyId = ApplicationDbContext.SeedPlay2DayCompanyId)
    {
        await using ApplicationDbContext ctx = new(harness.Options);

        Client client = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            Name = name,
            CreateDate = DateTime.UtcNow
        };

        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        return client.Id;
    }

    private static async Task<AutomationRule> SeedRuleAsync(
        Harness harness,
        Guid triggerClientId,
        AutomationTriggerType triggerType,
        params AutomationRuleCondition[] conditions)
    {
        await using ApplicationDbContext ctx = new(harness.Options);

        AutomationRule rule = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId,
            Name = $"{triggerType} rule",
            TriggerClientId = triggerClientId,
            TriggerType = triggerType,
            TriggerEvent = GameStatus.InGame,
            IdleThresholdMinutes = triggerType == AutomationTriggerType.ClientIdle ? 30 : null,
            ScheduledTimeOfDay = triggerType == AutomationTriggerType.Scheduled ? new TimeOnly(9, 0) : null,
            IsActive = true,
            IsEnabled = true,
            CreateDate = DateTime.UtcNow,
            Actions =
            [
                new AutomationRuleAction
                {
                    Id = Guid.NewGuid(),
                    ActionType = RecordingActionExecutor.TestActionType,
                    ParametersJson = "{}",
                    SortOrder = 0,
                    IsActive = true
                }
            ],
            Conditions = [.. conditions.Select((c, i) => { c.SortOrder = i; return c; })]
        };

        ctx.AutomationRules.Add(rule);
        await ctx.SaveChangesAsync();

        return rule;
    }

    private static async Task<List<AutomationRuleExecution>> GetExecutionsAsync(Harness harness)
    {
        await using ApplicationDbContext ctx = new(harness.Options);

        return await ctx.AutomationRuleExecutions
            .Include(e => e.ActionExecutions)
            .OrderBy(e => e.ExecutedAt)
            .ToListAsync();
    }

    private static void SetInGame(Harness harness, Guid clientId, bool inGame)
    {
        harness.Store.UpdateStatus(clientId, new LaserGameStatusDTO
        {
            Status = inGame ? GameStatus.InGame : GameStatus.NotStarted
        });
    }

    private static Task FireTransitionAsync(Harness harness, Guid clientId)
    {
        return harness.Engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(clientId, GameStatus.NotStarted, GameStatus.InGame),
            CancellationToken.None);
    }

    [TestMethod]
    public async Task Transition_RunsTheRule_WhenItsTimeWindowContainsTheVenueTime()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = Guid.NewGuid();

        // 15:00 UTC is 16:00 in London in July.
        await SeedRuleAsync(harness, kioskId, AutomationTriggerType.GameStatusTransition, TimeWindow(new(16, 0), new(17, 0)));

        await FireTransitionAsync(harness, kioskId);

        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
        Assert.IsTrue((await GetExecutionsAsync(harness)).Single().OverallSuccess);
    }

    [TestMethod]
    public async Task Transition_SkipsTheRule_AndLogsWhy_WhenOutsideTheTimeWindow()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = Guid.NewGuid();
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, AutomationTriggerType.GameStatusTransition, TimeWindow(new(15, 0), new(16, 0)));

        bool executedRaised = false;
        harness.Engine.OnRuleExecuted += (_, _, _) => executedRaised = true;

        await FireTransitionAsync(harness, kioskId);

        Assert.IsEmpty(harness.Executor.ExecutedActionIds, "No action may run when a condition isn't met.");

        AutomationRuleExecution skipped = (await GetExecutionsAsync(harness)).Single();
        Assert.AreEqual(rule.Id, skipped.AutomationRuleId);
        Assert.AreEqual(GameStatus.InGame.ToString(), skipped.TriggerEvent);
        Assert.IsFalse(skipped.OverallSuccess);
        Assert.IsEmpty(skipped.ActionExecutions);
        Assert.IsNotNull(skipped.SkipReason);
        StringAssert.Contains(skipped.SkipReason, "16:00");
        Assert.IsFalse(executedRaised, "A skip ran nothing, so it must not look like a run to the dashboard widget.");
    }

    [TestMethod]
    public async Task Transition_SkipConsumesTheEdge_AndTheNextEdgeIsCheckedAfresh()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = Guid.NewGuid();
        await SeedRuleAsync(harness, kioskId, AutomationTriggerType.GameStatusTransition, TimeWindow(new(16, 30), new(17, 0)));

        await FireTransitionAsync(harness, kioskId);
        Assert.IsEmpty(harness.Executor.ExecutedActionIds);

        harness.Clock.Advance(TimeSpan.FromMinutes(45));
        Assert.IsEmpty(harness.Executor.ExecutedActionIds, "The skipped edge must not be replayed once the window opens.");

        await FireTransitionAsync(harness, kioskId);
        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task Transition_ChecksAnotherKiosksLiveGameStatus()
    {
        Harness harness = await CreateHarnessAsync();
        Guid triggerKioskId = Guid.NewGuid();
        Guid arenaId = await SeedKioskAsync(harness, "Arena");
        await SeedRuleAsync(harness, triggerKioskId, AutomationTriggerType.GameStatusTransition, KioskInGame(arenaId, inGame: false));

        SetInGame(harness, arenaId, inGame: true);
        await FireTransitionAsync(harness, triggerKioskId);

        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
        Assert.AreEqual("Arena is in a game", (await GetExecutionsAsync(harness)).Single().SkipReason);

        SetInGame(harness, arenaId, inGame: false);
        await FireTransitionAsync(harness, triggerKioskId);

        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task Transition_TreatsADisconnectedKioskAsNotInGame()
    {
        Harness harness = await CreateHarnessAsync();
        Guid triggerKioskId = Guid.NewGuid();
        Guid arenaId = await SeedKioskAsync(harness, "Arena");
        await SeedRuleAsync(harness, triggerKioskId, AutomationTriggerType.GameStatusTransition, KioskInGame(arenaId, inGame: true));

        SetInGame(harness, arenaId, inGame: true);

        // What the hub does when a kiosk drops off mid-game.
        harness.Store.RemoveStatus(arenaId);

        await FireTransitionAsync(harness, triggerKioskId);

        Assert.IsEmpty(harness.Executor.ExecutedActionIds, "A kiosk that has gone offline must not count as still in a game.");
    }

    [TestMethod]
    public async Task Transition_RefusesAKioskConditionFromAnotherCompany()
    {
        Harness harness = await CreateHarnessAsync();
        Guid triggerKioskId = Guid.NewGuid();
        Guid otherCompanysKioskId = await SeedKioskAsync(harness, "Elsewhere", companyId: 2);
        await SeedRuleAsync(harness, triggerKioskId, AutomationTriggerType.GameStatusTransition, KioskInGame(otherCompanysKioskId, inGame: true));

        SetInGame(harness, otherCompanysKioskId, inGame: true);
        await FireTransitionAsync(harness, triggerKioskId);

        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
        StringAssert.Contains((await GetExecutionsAsync(harness)).Single().SkipReason, "another company");
    }

    [TestMethod]
    public async Task Transition_RunsARuleWhoseOnlyConditionWasRemoved()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = Guid.NewGuid();
        await SeedRuleAsync(harness, kioskId, AutomationTriggerType.GameStatusTransition, KioskInGame(Guid.NewGuid(), inGame: true, isActive: false));

        await FireTransitionAsync(harness, kioskId);

        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task Transition_RuleWithNoConditions_RunsExactlyAsBefore()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = Guid.NewGuid();
        await SeedRuleAsync(harness, kioskId, AutomationTriggerType.GameStatusTransition);

        await FireTransitionAsync(harness, kioskId);

        AutomationRuleExecution execution = (await GetExecutionsAsync(harness)).Single();
        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
        Assert.IsTrue(execution.OverallSuccess);
        Assert.IsNull(execution.SkipReason);
    }

    [TestMethod]
    public async Task Idle_StaysEligibleWhileSkipped_AndFiresOnceTheConditionHolds()
    {
        Harness harness = await CreateHarnessAsync();
        Guid idleKioskId = Guid.NewGuid();
        Guid arenaId = await SeedKioskAsync(harness, "Arena");
        await SeedRuleAsync(harness, idleKioskId, AutomationTriggerType.ClientIdle, KioskInGame(arenaId, inGame: false));

        harness.Engine.EnqueueTransition(idleKioskId, GameStatus.NotStarted);
        SetInGame(harness, arenaId, inGame: true);

        DateTime pastThreshold = DateTime.UtcNow.AddMinutes(31);

        // Several ticks while the condition fails: still idle, still eligible, logged only once.
        await harness.Engine.ProcessIdleRulesAsync(pastThreshold, CancellationToken.None);
        await harness.Engine.ProcessIdleRulesAsync(pastThreshold.AddMinutes(1), CancellationToken.None);
        await harness.Engine.ProcessIdleRulesAsync(pastThreshold.AddMinutes(2), CancellationToken.None);

        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
        List<AutomationRuleExecution> afterSkips = await GetExecutionsAsync(harness);
        Assert.HasCount(1, afterSkips, "One skip row per idle stretch, not one per tick.");
        Assert.IsNotNull(afterSkips[0].SkipReason);

        SetInGame(harness, arenaId, inGame: false);
        await harness.Engine.ProcessIdleRulesAsync(pastThreshold.AddMinutes(3), CancellationToken.None);

        Assert.HasCount(1, harness.Executor.ExecutedActionIds, "The rule must fire in the same idle stretch once its condition holds.");

        // And, having fired, it doesn't fire again in this stretch.
        await harness.Engine.ProcessIdleRulesAsync(pastThreshold.AddMinutes(4), CancellationToken.None);
        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task Scheduled_LogsOneSkipPerDay_AndStillFiresLaterInTheWindow()
    {
        Harness harness = await CreateHarnessAsync();
        Guid associatedKioskId = Guid.NewGuid();
        Guid arenaId = await SeedKioskAsync(harness, "Arena");
        await SeedRuleAsync(harness, associatedKioskId, AutomationTriggerType.Scheduled, KioskInGame(arenaId, inGame: false));

        SetInGame(harness, arenaId, inGame: true);

        DateTime nineAm = DateTime.Today.AddHours(9);

        await harness.Engine.ProcessScheduledRulesAsync(nineAm, CancellationToken.None);
        await harness.Engine.ProcessScheduledRulesAsync(nineAm.AddMinutes(1), CancellationToken.None);

        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
        Assert.HasCount(1, await GetExecutionsAsync(harness), "One skip row per day, not one per tick.");

        // The game finishes inside the catch-up window, so the morning rule still runs.
        SetInGame(harness, arenaId, inGame: false);
        await harness.Engine.ProcessScheduledRulesAsync(nineAm.AddMinutes(2), CancellationToken.None);

        Assert.HasCount(1, harness.Executor.ExecutedActionIds);

        await harness.Engine.ProcessScheduledRulesAsync(nineAm.AddMinutes(3), CancellationToken.None);
        Assert.HasCount(1, harness.Executor.ExecutedActionIds, "Once it has fired, the day is used up as before.");
    }

    [TestMethod]
    public async Task Scheduled_ASkipRowDoesNotCountAsFiredAfterARestart()
    {
        Harness first = await CreateHarnessAsync();
        Guid associatedKioskId = Guid.NewGuid();
        Guid arenaId = await SeedKioskAsync(first, "Arena");
        await SeedRuleAsync(first, associatedKioskId, AutomationTriggerType.Scheduled, KioskInGame(arenaId, inGame: false));

        SetInGame(first, arenaId, inGame: true);
        DateTime nineAm = DateTime.Today.AddHours(9);
        await first.Engine.ProcessScheduledRulesAsync(nineAm, CancellationToken.None);
        Assert.IsEmpty(first.Executor.ExecutedActionIds);

        // A restart builds a fresh engine that reseeds "last fired" from the log.
        Mock<ISystemDbContextFactory> factoryMock = new();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(first.Options));
        RecordingActionExecutor executor = new();
        AutomationEngineService restarted = new(factoryMock.Object, [executor], new LaserGameStatusStore(), first.Clock, NullLogger<AutomationEngineService>.Instance);

        await restarted.ProcessScheduledRulesAsync(nineAm.AddMinutes(1), CancellationToken.None);

        Assert.HasCount(1, executor.ExecutedActionIds, "A skip row must not be mistaken for today's successful run.");
    }
}
