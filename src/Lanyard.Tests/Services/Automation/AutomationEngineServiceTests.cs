#nullable enable

using System.Diagnostics;
using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

[TestClass]
public class AutomationEngineServiceTests
{
    private const string EnabledSettingKey = "AutomationEngine.Enabled";

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static IDbContextFactory<ApplicationDbContext> GetFactory(
        DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();

        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock.Object;
    }

    private static AutomationEngineService GetEngine(
        DbContextOptions<ApplicationDbContext> options,
        params IActionExecutor[] executors)
    {
        return new AutomationEngineService(
            GetFactory(options),
            executors,
            NullLogger<AutomationEngineService>.Instance);
    }

    // The engine reads its enabled flag from AppSettings on the first ProcessTransitionAsync and
    // overwrites whatever SetEnabled() left behind, so tests have to seed the row rather than call
    // SetEnabled.
    private static async Task SeedEngineEnabledAsync(
        DbContextOptions<ApplicationDbContext> options, bool enabled)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.AppSettings.Add(new AppSetting
        {
            Id = Guid.NewGuid(),
            Key = EnabledSettingKey,
            Value = enabled ? "true" : "false",
            CreateDate = DateTime.UtcNow
        });

        await ctx.SaveChangesAsync();
    }

    private static async Task<AutomationRule> SeedRuleAsync(
        DbContextOptions<ApplicationDbContext> options,
        Guid triggerClientId,
        GameStatus triggerEvent,
        int actionCount = 1,
        bool isEnabled = true,
        bool isActive = true)
    {
        await using ApplicationDbContext ctx = new(options);

        AutomationRule rule = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Rule for {triggerEvent}",
            TriggerClientId = triggerClientId,
            TriggerEvent = triggerEvent,
            IsActive = isActive,
            IsEnabled = isEnabled,
            CreateDate = DateTime.UtcNow,
            Actions = [.. Enumerable.Range(0, actionCount).Select(i => new AutomationRuleAction
            {
                Id = Guid.NewGuid(),
                ActionType = RecordingActionExecutor.TestActionType,
                ParametersJson = "{}",
                SortOrder = i,
                IsActive = true
            })]
        };

        ctx.AutomationRules.Add(rule);
        await ctx.SaveChangesAsync();

        return rule;
    }

    [TestMethod]
    public void EnqueueTransition_ShouldNotWriteToChannel_WhenStatusIsUnchanged()
    {
        AutomationEngineService engine = GetEngine(GetInMemoryOptions());

        // The engine seeds an unseen client at NotStarted, so reporting NotStarted is not a
        // transition. This is the edge-triggered guard: a kiosk that keeps repeating its current
        // status must not re-fire the rules attached to it.
        engine.EnqueueTransition(Guid.NewGuid(), GameStatus.NotStarted);

        Assert.IsFalse(
            engine.Reader.TryRead(out _),
            "Reporting the status a client is already in must not queue a transition.");
    }

    [TestMethod]
    public void EnqueueTransition_ShouldWriteToChannel_WhenStatusChanges()
    {
        AutomationEngineService engine = GetEngine(GetInMemoryOptions());
        Guid clientId = Guid.NewGuid();

        engine.EnqueueTransition(clientId, GameStatus.InGame);

        Assert.IsTrue(engine.Reader.TryRead(out GameStatusTransitionEvent? ev));
        Assert.IsNotNull(ev);
        Assert.AreEqual(clientId, ev.ClientId);
        Assert.AreEqual(GameStatus.NotStarted, ev.PreviousStatus);
        Assert.AreEqual(GameStatus.InGame, ev.NewStatus);

        // Repeating the new status is now the unchanged case.
        engine.EnqueueTransition(clientId, GameStatus.InGame);

        Assert.IsFalse(engine.Reader.TryRead(out _));
    }

    [TestMethod]
    public void EnqueueTransition_ShouldReturnSynchronously()
    {
        AutomationEngineService engine = GetEngine(GetInMemoryOptions());
        Guid clientId = Guid.NewGuid();

        // Called from the SignalR hub on the request path, so it has to hand off to the channel
        // rather than wait on anything. Nothing is reading this channel, and it must still return.
        Stopwatch stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < 1_000; i++)
        {
            engine.EnqueueTransition(clientId, i % 2 == 0 ? GameStatus.InGame : GameStatus.NotStarted);
        }

        stopwatch.Stop();

        Assert.IsLessThan(
            2_000,
            stopwatch.ElapsedMilliseconds,
            "EnqueueTransition blocked; it must not wait for a consumer.");
    }

    [TestMethod]
    public async Task ProcessTransitionAsync_ShouldExecuteMatchingRules_WhenStatusIsInGame()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await SeedEngineEnabledAsync(options, enabled: true);
        AutomationRule rule = await SeedRuleAsync(options, clientId, GameStatus.InGame);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(clientId, GameStatus.NotStarted, GameStatus.InGame),
            CancellationToken.None);

        Assert.HasCount(1, executor.ExecutedActionIds);
        Assert.AreEqual(rule.Actions[0].Id, executor.ExecutedActionIds[0]);
    }

    [TestMethod]
    public async Task ProcessTransitionAsync_ShouldExecuteMatchingRules_WhenStatusIsNotStarted()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await SeedEngineEnabledAsync(options, enabled: true);
        await SeedRuleAsync(options, clientId, GameStatus.NotStarted);

        // A rule on a different status must not fire for this transition.
        await SeedRuleAsync(options, clientId, GameStatus.InGame);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(clientId, GameStatus.InGame, GameStatus.NotStarted),
            CancellationToken.None);

        Assert.HasCount(1, executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task ProcessTransitionAsync_ShouldNotExecuteRules_ForADifferentClient()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await SeedEngineEnabledAsync(options, enabled: true);
        await SeedRuleAsync(options, Guid.NewGuid(), GameStatus.InGame);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(Guid.NewGuid(), GameStatus.NotStarted, GameStatus.InGame),
            CancellationToken.None);

        Assert.IsEmpty(
            executor.ExecutedActionIds,
            "A rule bound to one kiosk must not fire for another kiosk's transition.");
    }

    [TestMethod]
    public async Task ProcessTransitionAsync_ShouldContinueRemainingActions_WhenOneActionFails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await SeedEngineEnabledAsync(options, enabled: true);
        AutomationRule rule = await SeedRuleAsync(options, clientId, GameStatus.InGame, actionCount: 3);

        Guid failingActionId = rule.Actions.Single(a => a.SortOrder == 1).Id;

        // Thrown, not returned as a failed result: an executor blowing up must not take the rest of
        // the rule down with it.
        RecordingActionExecutor executor = new(action =>
            action.Id == failingActionId
                ? throw new InvalidOperationException("boom")
                : (true, null));

        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(clientId, GameStatus.NotStarted, GameStatus.InGame),
            CancellationToken.None);

        Assert.HasCount(
            3,
            executor.ExecutedActionIds,
            "Every action should still be attempted after one of them throws.");

        await using ApplicationDbContext ctx = new(options);

        AutomationRuleExecution execution = await ctx.AutomationRuleExecutions
            .Include(e => e.ActionExecutions)
            .SingleAsync();

        Assert.IsFalse(execution.OverallSuccess);
        Assert.HasCount(3, execution.ActionExecutions);
        Assert.HasCount(1, execution.ActionExecutions.Where(a => !a.Success).ToList());

        AutomationRuleActionExecution failed = execution.ActionExecutions.Single(a => !a.Success);

        Assert.AreEqual(failingActionId, failed.AutomationRuleActionId);
        Assert.IsNotNull(failed.ErrorMessage);
        Assert.Contains("boom", failed.ErrorMessage);
    }

    [TestMethod]
    public async Task ProcessTransitionAsync_ShouldRecordActionTypeNotSupported_WhenNoExecutorHandlesIt()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await SeedEngineEnabledAsync(options, enabled: true);
        await SeedRuleAsync(options, clientId, GameStatus.InGame);

        // No executors registered at all - the "added an action type and forgot to register its
        // executor" case the automation-engine skill warns fails silently.
        AutomationEngineService engine = GetEngine(options);

        await engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(clientId, GameStatus.NotStarted, GameStatus.InGame),
            CancellationToken.None);

        await using ApplicationDbContext ctx = new(options);

        AutomationRuleExecution execution = await ctx.AutomationRuleExecutions
            .Include(e => e.ActionExecutions)
            .SingleAsync();

        Assert.IsFalse(execution.OverallSuccess);

        AutomationRuleActionExecution actionExecution = execution.ActionExecutions.Single();

        Assert.IsFalse(actionExecution.Success);
        Assert.IsNotNull(actionExecution.ErrorMessage);
        Assert.Contains("Action type not supported", actionExecution.ErrorMessage);
    }

    [TestMethod]
    public async Task ProcessTransitionAsync_ShouldSkipAllRules_WhenEngineIsDisabled()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await SeedEngineEnabledAsync(options, enabled: false);
        await SeedRuleAsync(options, clientId, GameStatus.InGame);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(clientId, GameStatus.NotStarted, GameStatus.InGame),
            CancellationToken.None);

        Assert.IsEmpty(executor.ExecutedActionIds);

        await using ApplicationDbContext ctx = new(options);

        Assert.IsFalse(
            await ctx.AutomationRuleExecutions.AnyAsync(),
            "A disabled engine should not write execution logs either.");
    }

    [TestMethod]
    public async Task ProcessTransitionAsync_ShouldSkipRules_ThatAreDisabledOrInactive()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await SeedEngineEnabledAsync(options, enabled: true);
        await SeedRuleAsync(options, clientId, GameStatus.InGame, isEnabled: false);
        await SeedRuleAsync(options, clientId, GameStatus.InGame, isActive: false);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(clientId, GameStatus.NotStarted, GameStatus.InGame),
            CancellationToken.None);

        Assert.IsEmpty(
            executor.ExecutedActionIds,
            "The rule cache only loads rules that are both active and enabled.");
    }

    [TestMethod]
    public async Task ProcessTransitionAsync_ShouldReloadRuleCache_WhenCacheDirtyFlagIsTrue()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await SeedEngineEnabledAsync(options, enabled: true);
        await SeedRuleAsync(options, clientId, GameStatus.InGame);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        GameStatusTransitionEvent ev = new(clientId, GameStatus.NotStarted, GameStatus.InGame);

        await engine.ProcessTransitionAsync(ev, CancellationToken.None);
        Assert.HasCount(1, executor.ExecutedActionIds);

        // Written straight to the database, behind the engine's back, so the cache is now stale.
        await SeedRuleAsync(options, clientId, GameStatus.InGame);

        await engine.ProcessTransitionAsync(ev, CancellationToken.None);

        Assert.HasCount(
            2,
            executor.ExecutedActionIds,
            "Without an invalidation the engine should still be serving the cached rule set, so "
            + "the second pass should run the original rule only.");
    }

    [TestMethod]
    public async Task InvalidateRuleCache_ShouldSetDirtyFlag()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await SeedEngineEnabledAsync(options, enabled: true);
        await SeedRuleAsync(options, clientId, GameStatus.InGame);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        GameStatusTransitionEvent ev = new(clientId, GameStatus.NotStarted, GameStatus.InGame);

        await engine.ProcessTransitionAsync(ev, CancellationToken.None);
        Assert.HasCount(1, executor.ExecutedActionIds);

        await SeedRuleAsync(options, clientId, GameStatus.InGame);

        // The dirty flag is private, so this asserts the effect callers actually depend on: after
        // invalidating, the next transition sees rules added since the cache was built.
        engine.InvalidateRuleCache();

        await engine.ProcessTransitionAsync(ev, CancellationToken.None);

        Assert.HasCount(
            3,
            executor.ExecutedActionIds,
            "After invalidating, the second pass should run both rules (1 + 2 = 3 executions).");
    }

    [TestMethod]
    public async Task ProcessTransitionAsync_ShouldWriteExecutionLog_AfterRuleExecutes()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await SeedEngineEnabledAsync(options, enabled: true);
        AutomationRule rule = await SeedRuleAsync(options, clientId, GameStatus.InGame, actionCount: 2);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        DateTime before = DateTime.UtcNow;

        await engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(clientId, GameStatus.NotStarted, GameStatus.InGame),
            CancellationToken.None);

        await using ApplicationDbContext ctx = new(options);

        AutomationRuleExecution execution = await ctx.AutomationRuleExecutions
            .Include(e => e.ActionExecutions)
            .SingleAsync();

        Assert.AreEqual(rule.Id, execution.AutomationRuleId);
        Assert.AreEqual(rule.Name, execution.RuleName);
        Assert.AreEqual(clientId, execution.TriggerClientId);
        // Stored as a string snapshot so a later enum rename can't rewrite history.
        Assert.AreEqual(nameof(GameStatus.InGame), execution.TriggerEvent);
        Assert.IsTrue(execution.OverallSuccess);
        Assert.HasCount(2, execution.ActionExecutions);
        Assert.IsGreaterThanOrEqualTo(before, execution.ExecutedAt);
    }

    [TestMethod]
    public async Task ProcessTransitionAsync_ShouldRaiseOnRuleExecuted_AfterRuleExecutes()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await SeedEngineEnabledAsync(options, enabled: true);
        AutomationRule rule = await SeedRuleAsync(options, clientId, GameStatus.InGame);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        List<(Guid RuleId, bool Success)> raised = [];
        engine.OnRuleExecuted += (ruleId, _, success) => raised.Add((ruleId, success));

        await engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(clientId, GameStatus.NotStarted, GameStatus.InGame),
            CancellationToken.None);

        // The automation UI updates off this event; if it stops firing the page silently shows
        // stale "last run" information.
        Assert.HasCount(1, raised);
        Assert.AreEqual(rule.Id, raised[0].RuleId);
        Assert.IsTrue(raised[0].Success);
    }
}
