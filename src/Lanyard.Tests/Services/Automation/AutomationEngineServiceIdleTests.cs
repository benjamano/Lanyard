using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

/// <summary>
/// Idle-trigger behaviour. The timer loop in IdleTriggerHostedService is deliberately left
/// untested (as AutomationEngineHostedService is); ProcessIdleRulesAsync takes "now" as a
/// parameter so the interesting behaviour can be driven at fixed instants instead.
/// </summary>
[TestClass]
public class AutomationEngineServiceIdleTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static IDbContextFactory<ApplicationDbContext> GetFactory(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock.Object;
    }

    private static AutomationEngineService GetEngine(
        DbContextOptions<ApplicationDbContext> options,
        params IActionExecutor[] executors)
    {
        return new AutomationEngineService(
            GetFactory(options), executors, NullLogger<AutomationEngineService>.Instance);
    }

    // The engine reads its enabled flag from AppSettings on first use and overwrites whatever
    // SetEnabled() left behind, so tests seed the row instead.
    private static async Task SeedEngineEnabledAsync(DbContextOptions<ApplicationDbContext> options, bool enabled)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.AppSettings.Add(new AppSetting
        {
            Id = Guid.NewGuid(),
            Key = "AutomationEngine.Enabled",
            Value = enabled ? "true" : "false"
        });

        await ctx.SaveChangesAsync();
    }

    private static async Task SeedIdleRuleAsync(
        DbContextOptions<ApplicationDbContext> options,
        Guid triggerClientId,
        int? idleThresholdMinutes,
        AutomationTriggerType triggerType = AutomationTriggerType.ClientIdle)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.AutomationRules.Add(new AutomationRule
        {
            Id = Guid.NewGuid(),
            Name = "Idle rule",
            TriggerClientId = triggerClientId,
            TriggerType = triggerType,
            TriggerEvent = GameStatus.NotStarted,
            IdleThresholdMinutes = idleThresholdMinutes,
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
            ]
        });

        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public void IsIdleThresholdReached_IsFalseBeforeTheThreshold()
    {
        DateTime lastTransition = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

        Assert.IsFalse(AutomationEngineService.IsIdleThresholdReached(
            lastTransition, lastTransition.AddMinutes(29), 30));
    }

    [TestMethod]
    public void IsIdleThresholdReached_IsTrueExactlyOnTheThreshold()
    {
        DateTime lastTransition = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

        Assert.IsTrue(AutomationEngineService.IsIdleThresholdReached(
            lastTransition, lastTransition.AddMinutes(30), 30));
    }

    [TestMethod]
    public void IsIdleThresholdReached_IsFalseForANonPositiveThreshold()
    {
        DateTime lastTransition = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

        Assert.IsFalse(AutomationEngineService.IsIdleThresholdReached(
            lastTransition, lastTransition.AddDays(1), 0));
    }

    [TestMethod]
    public async Task ProcessIdleRules_DoesNotFireBeforeTheThresholdIsReached()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        await SeedIdleRuleAsync(options, clientId, 30);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        engine.EnqueueTransition(clientId, GameStatus.NotStarted);

        await engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddMinutes(10), CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessIdleRules_FiresOnceWhenTheThresholdIsReached()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        await SeedIdleRuleAsync(options, clientId, 30);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        engine.EnqueueTransition(clientId, GameStatus.NotStarted);

        DateTime idleAt = DateTime.UtcNow.AddMinutes(31);

        await engine.ProcessIdleRulesAsync(idleAt, CancellationToken.None);

        Assert.AreEqual(1, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessIdleRules_DoesNotRefireOnSubsequentTicksInTheSameIdleStretch()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        await SeedIdleRuleAsync(options, clientId, 30);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        engine.EnqueueTransition(clientId, GameStatus.NotStarted);

        await engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddMinutes(31), CancellationToken.None);
        await engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddMinutes(32), CancellationToken.None);
        await engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddMinutes(90), CancellationToken.None);

        Assert.AreEqual(1, executor.ExecutedActionIds.Count, "The idle stretch should only fire once.");
    }

    [TestMethod]
    public async Task ProcessIdleRules_BecomesEligibleAgainAfterAGameStarts()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        await SeedIdleRuleAsync(options, clientId, 30);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        engine.EnqueueTransition(clientId, GameStatus.NotStarted);
        await engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddMinutes(31), CancellationToken.None);

        Assert.AreEqual(1, executor.ExecutedActionIds.Count);

        // A game starting resets the stretch, and the next quiet spell should fire again.
        engine.EnqueueTransition(clientId, GameStatus.InGame);
        engine.EnqueueTransition(clientId, GameStatus.NotStarted);

        await engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddMinutes(62), CancellationToken.None);

        Assert.AreEqual(2, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessIdleRules_DoesNotFireForAClientTheServerHasNeverHeardFrom()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        await SeedIdleRuleAsync(options, Guid.NewGuid(), 30);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddDays(1), CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessIdleRules_IgnoresGameStatusTransitionRules()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        await SeedIdleRuleAsync(options, clientId, 30, AutomationTriggerType.GameStatusTransition);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        engine.EnqueueTransition(clientId, GameStatus.NotStarted);

        await engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddMinutes(31), CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessTransition_IgnoresIdleRulesEvenWhenTheirStaleTriggerEventMatches()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();

        // The seeded idle rule carries TriggerEvent = NotStarted. Without the TriggerType clause
        // in ProcessTransitionAsync it would also fire on this transition.
        await SeedIdleRuleAsync(options, clientId, 30);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(clientId, GameStatus.InGame, GameStatus.NotStarted),
            CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessIdleRules_DoesNothingWhenTheEngineIsDisabled()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, false);

        Guid clientId = Guid.NewGuid();
        await SeedIdleRuleAsync(options, clientId, 30);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        engine.EnqueueTransition(clientId, GameStatus.NotStarted);

        await engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddMinutes(31), CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessIdleRules_WritesAnExecutionLogLabelledAsAnIdleTrigger()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        await SeedIdleRuleAsync(options, clientId, 30);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        engine.EnqueueTransition(clientId, GameStatus.NotStarted);

        await engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddMinutes(31), CancellationToken.None);

        await using ApplicationDbContext ctx = new(options);

        AutomationRuleExecution execution = await ctx.AutomationRuleExecutions.SingleAsync();

        Assert.AreEqual(nameof(AutomationTriggerType.ClientIdle), execution.TriggerEvent);
        Assert.AreEqual(clientId, execution.TriggerClientId);
        Assert.IsTrue(execution.OverallSuccess);
    }
}
