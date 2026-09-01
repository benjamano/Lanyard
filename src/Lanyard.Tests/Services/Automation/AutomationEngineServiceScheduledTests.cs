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
/// Scheduled-trigger behaviour. Mirrors AutomationEngineServiceIdleTests: the timer loop in
/// ScheduledTriggerHostedService is deliberately left untested, since ProcessScheduledRulesAsync
/// takes "now" as a parameter so the interesting behaviour can be driven at fixed instants instead.
/// Engine-level tests use DateTime.Now-relative deltas rather than a hardcoded calendar date, so
/// they don't depend on which timezone the machine running them is in.
/// </summary>
[TestClass]
public class AutomationEngineServiceScheduledTests
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

    private static async Task<Guid> SeedScheduledRuleAsync(
        DbContextOptions<ApplicationDbContext> options,
        Guid triggerClientId,
        TimeOnly scheduledTimeOfDay,
        string? scheduledDaysOfWeek = null,
        string name = "Scheduled rule",
        Guid? ruleId = null)
    {
        Guid id = ruleId ?? Guid.NewGuid();

        await using ApplicationDbContext ctx = new(options);

        ctx.AutomationRules.Add(new AutomationRule
        {
            Id = id,
            Name = name,
            TriggerClientId = triggerClientId,
            TriggerType = AutomationTriggerType.Scheduled,
            TriggerEvent = GameStatus.NotStarted,
            ScheduledTimeOfDay = scheduledTimeOfDay,
            ScheduledDaysOfWeek = scheduledDaysOfWeek,
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

        return id;
    }

    private static async Task SeedExecutionAsync(
        DbContextOptions<ApplicationDbContext> options,
        Guid ruleId,
        Guid triggerClientId,
        DateTime executedAtUtc,
        bool overallSuccess)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.AutomationRuleExecutions.Add(new AutomationRuleExecution
        {
            Id = Guid.NewGuid(),
            AutomationRuleId = ruleId,
            RuleName = "Scheduled rule",
            ExecutedAt = executedAtUtc,
            TriggerEvent = nameof(AutomationTriggerType.Scheduled),
            TriggerClientId = triggerClientId,
            OverallSuccess = overallSuccess
        });

        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public void IsScheduledTimeReached_IsFalseBeforeTheScheduledTime()
    {
        TimeOnly scheduledTime = new(9, 0);
        DateTime nowLocal = new(2026, 8, 26, 8, 59, 0);

        Assert.IsFalse(AutomationEngineService.IsScheduledTimeReached(scheduledTime, nowLocal, TimeSpan.FromMinutes(5)));
    }

    [TestMethod]
    public void IsScheduledTimeReached_IsTrueExactlyAtTheScheduledTime()
    {
        TimeOnly scheduledTime = new(9, 0);
        DateTime nowLocal = new(2026, 8, 26, 9, 0, 0);

        Assert.IsTrue(AutomationEngineService.IsScheduledTimeReached(scheduledTime, nowLocal, TimeSpan.FromMinutes(5)));
    }

    [TestMethod]
    public void IsScheduledTimeReached_IsTrueWithinTheCatchUpWindow()
    {
        TimeOnly scheduledTime = new(9, 0);
        DateTime nowLocal = new(2026, 8, 26, 9, 3, 0);

        Assert.IsTrue(AutomationEngineService.IsScheduledTimeReached(scheduledTime, nowLocal, TimeSpan.FromMinutes(5)));
    }

    [TestMethod]
    public void IsScheduledTimeReached_IsFalseAfterTheCatchUpWindow()
    {
        TimeOnly scheduledTime = new(9, 0);
        DateTime nowLocal = new(2026, 8, 26, 9, 6, 0);

        Assert.IsFalse(AutomationEngineService.IsScheduledTimeReached(scheduledTime, nowLocal, TimeSpan.FromMinutes(5)));
    }

    [TestMethod]
    public void IsScheduledTimeReached_DoesNotWrapAroundMidnight()
    {
        TimeOnly scheduledTime = new(23, 58);
        DateTime nowLocal = new(2026, 8, 27, 0, 2, 0); // next calendar day, "4 minutes later" in wall-clock terms

        Assert.IsFalse(AutomationEngineService.IsScheduledTimeReached(scheduledTime, nowLocal, TimeSpan.FromMinutes(5)),
            "A near-midnight rule must not catch up into the next calendar day.");
    }

    [TestMethod]
    public void IsScheduledDayMatched_MatchesAListedDay()
    {
        Assert.IsTrue(AutomationEngineService.IsScheduledDayMatched("1,3,5", DayOfWeek.Wednesday));
    }

    [TestMethod]
    public void IsScheduledDayMatched_DoesNotMatchAnUnlistedDay()
    {
        Assert.IsFalse(AutomationEngineService.IsScheduledDayMatched("1,3,5", DayOfWeek.Sunday));
    }

    [TestMethod]
    public void IsScheduledDayMatched_TreatsNullAndEmptyAsEveryDay()
    {
        Assert.IsTrue(AutomationEngineService.IsScheduledDayMatched(null, DayOfWeek.Sunday));
        Assert.IsTrue(AutomationEngineService.IsScheduledDayMatched(string.Empty, DayOfWeek.Saturday));
    }

    [TestMethod]
    public void IsScheduledDayMatched_IsFalseForAMalformedValue()
    {
        Assert.IsFalse(AutomationEngineService.IsScheduledDayMatched("Monday", DayOfWeek.Monday),
            "A corrupt value must fail closed rather than falling back to 'every day'.");
    }

    [TestMethod]
    public async Task ProcessScheduledRules_FiresOnceAtTheScheduledTime()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        DateTime nowLocal = DateTime.Now;
        await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(nowLocal));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(nowLocal, CancellationToken.None);

        Assert.AreEqual(1, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessScheduledRules_DoesNotFireBeforeTheScheduledTime()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        DateTime nowLocal = DateTime.Now;
        await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(nowLocal.AddHours(1)));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(nowLocal, CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessScheduledRules_DoesNotRefireOnSubsequentTicksTheSameDay()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        DateTime nowLocal = DateTime.Now;
        await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(nowLocal));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(nowLocal, CancellationToken.None);
        await engine.ProcessScheduledRulesAsync(nowLocal.AddMinutes(1), CancellationToken.None);
        await engine.ProcessScheduledRulesAsync(nowLocal.AddMinutes(3), CancellationToken.None);

        Assert.AreEqual(1, executor.ExecutedActionIds.Count,
            "The rule should only fire once per calendar day, even on later ticks still inside the catch-up window.");
    }

    [TestMethod]
    public async Task ProcessScheduledRules_FiresAgainTheFollowingDay()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        DateTime nowLocal = DateTime.Now;
        await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(nowLocal));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(nowLocal, CancellationToken.None);
        Assert.AreEqual(1, executor.ExecutedActionIds.Count);

        await engine.ProcessScheduledRulesAsync(nowLocal.AddDays(1), CancellationToken.None);
        Assert.AreEqual(2, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessScheduledRules_DoesNotFireOnAnUnlistedDayOfWeek()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        DateTime nowLocal = DateTime.Now;
        DayOfWeek tomorrow = nowLocal.AddDays(1).DayOfWeek;

        // Only tomorrow is listed, so today must not fire even though the time matches exactly.
        await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(nowLocal), ((int)tomorrow).ToString());

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(nowLocal, CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessScheduledRules_RetriesWithinTheCatchUpWindowAfterAFailure()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        DateTime nowLocal = DateTime.Now;
        await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(nowLocal));

        // Stands in for a kiosk that was offline when its scheduled action was due.
        RecordingActionExecutor executor = new(_ => (false, "Client not connected"));
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(nowLocal, CancellationToken.None);
        Assert.AreEqual(1, executor.ExecutedActionIds.Count);

        await engine.ProcessScheduledRulesAsync(nowLocal.AddMinutes(1), CancellationToken.None);
        Assert.AreEqual(2, executor.ExecutedActionIds.Count,
            "A failed attempt should retry on the next tick within the catch-up window.");
    }

    [TestMethod]
    public async Task ProcessScheduledRules_StopsRetryingOnceAnAttemptSucceeds()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        DateTime nowLocal = DateTime.Now;
        await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(nowLocal));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(nowLocal, CancellationToken.None);
        await engine.ProcessScheduledRulesAsync(nowLocal.AddMinutes(2), CancellationToken.None);
        await engine.ProcessScheduledRulesAsync(nowLocal.AddMinutes(4), CancellationToken.None);

        Assert.AreEqual(1, executor.ExecutedActionIds.Count,
            "A successful fire should not be retried on later ticks within the same catch-up window.");
    }

    [TestMethod]
    public async Task ProcessScheduledRules_DoesNothingWhenTheEngineIsDisabled()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, false);

        Guid clientId = Guid.NewGuid();
        DateTime nowLocal = DateTime.Now;
        await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(nowLocal));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(nowLocal, CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessScheduledRules_IgnoresIdleAndTransitionRules()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.AutomationRules.AddRange(
                new AutomationRule
                {
                    Id = Guid.NewGuid(),
                    Name = "Idle rule",
                    TriggerClientId = clientId,
                    TriggerType = AutomationTriggerType.ClientIdle,
                    TriggerEvent = GameStatus.NotStarted,
                    IdleThresholdMinutes = 30,
                    IsActive = true,
                    IsEnabled = true,
                    CreateDate = DateTime.UtcNow,
                    Actions = [new AutomationRuleAction { Id = Guid.NewGuid(), ActionType = RecordingActionExecutor.TestActionType, ParametersJson = "{}", SortOrder = 0, IsActive = true }]
                },
                new AutomationRule
                {
                    Id = Guid.NewGuid(),
                    Name = "Transition rule",
                    TriggerClientId = clientId,
                    TriggerType = AutomationTriggerType.GameStatusTransition,
                    TriggerEvent = GameStatus.InGame,
                    IsActive = true,
                    IsEnabled = true,
                    CreateDate = DateTime.UtcNow,
                    Actions = [new AutomationRuleAction { Id = Guid.NewGuid(), ActionType = RecordingActionExecutor.TestActionType, ParametersJson = "{}", SortOrder = 0, IsActive = true }]
                });

            await ctx.SaveChangesAsync();
        }

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(DateTime.Now, CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessIdleRules_IgnoresScheduledRules()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(DateTime.Now));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        engine.EnqueueTransition(clientId, GameStatus.NotStarted);

        await engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddDays(1), CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessTransition_IgnoresScheduledRulesEvenWhenTheirStaleTriggerEventMatches()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();

        // The seeded scheduled rule carries TriggerEvent = NotStarted. Without the TriggerType
        // clause in ProcessTransitionAsync it would also fire on this transition.
        await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(DateTime.Now));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessTransitionAsync(
            new GameStatusTransitionEvent(clientId, GameStatus.InGame, GameStatus.NotStarted),
            CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public async Task ProcessScheduledRules_WritesAnExecutionLogLabelledAsAScheduledTrigger()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        DateTime nowLocal = DateTime.Now;
        await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(nowLocal));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(nowLocal, CancellationToken.None);

        await using ApplicationDbContext ctx = new(options);

        AutomationRuleExecution execution = await ctx.AutomationRuleExecutions.SingleAsync();

        Assert.AreEqual(nameof(AutomationTriggerType.Scheduled), execution.TriggerEvent);
        Assert.AreEqual(clientId, execution.TriggerClientId);
        Assert.IsTrue(execution.OverallSuccess);
    }

    [TestMethod]
    public async Task ProcessScheduledRules_DoesNotRefireAfterARestartOnceItHasAlreadyFiredToday()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        DateTime nowLocal = DateTime.Now;
        Guid ruleId = await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(nowLocal));

        // A prior successful execution earlier today, as if the rule already fired before a restart.
        await SeedExecutionAsync(options, ruleId, clientId, DateTime.UtcNow, overallSuccess: true);

        RecordingActionExecutor executor = new();
        // Fresh engine instance - its in-memory dictionaries start empty, exactly like a restart.
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(nowLocal.AddMinutes(1), CancellationToken.None);

        Assert.AreEqual(0, executor.ExecutedActionIds.Count,
            "A restart must not forget that the rule already fired successfully today.");
    }

    [TestMethod]
    public async Task ProcessScheduledRules_StillRetriesAfterARestartIfTheOnlyPriorExecutionFailed()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options, true);

        Guid clientId = Guid.NewGuid();
        DateTime nowLocal = DateTime.Now;
        Guid ruleId = await SeedScheduledRuleAsync(options, clientId, TimeOnly.FromDateTime(nowLocal));

        await SeedExecutionAsync(options, ruleId, clientId, DateTime.UtcNow, overallSuccess: false);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, executor);

        await engine.ProcessScheduledRulesAsync(nowLocal.AddMinutes(1), CancellationToken.None);

        Assert.AreEqual(1, executor.ExecutedActionIds.Count,
            "A restart after only a failed attempt should still allow the catch-up retry.");
    }
}
