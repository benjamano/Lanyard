#nullable enable

using Lanyard.Application.Services;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

[TestClass]
public class AutomationRuleServiceTests
{
    private const string EnabledSettingKey = "AutomationEngine.Enabled";

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static ISystemDbContextFactory GetFactory(
        DbContextOptions<ApplicationDbContext> options)
    {
        Mock<ISystemDbContextFactory> factoryMock = new();

        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock.Object;
    }

    // A real AutomationEngineService, not a mock. AutomationRuleService takes the concrete type and
    // InvalidateRuleCache is not virtual, so Moq cannot stand in for it or verify the call - and
    // AutomationEngineService has no parameterless constructor for Moq to use either.
    //
    // Asserting on the real thing is the better test anyway: the contract that matters is not "a
    // method was called", it is "the engine stops serving a stale rule set", which the
    // *_ShouldInvalidateRuleCache tests below check by running a transition afterwards.
    private static (AutomationRuleService Rules, AutomationEngineService Engine, RecordingActionExecutor Executor)
        GetService(DbContextOptions<ApplicationDbContext> options)
    {
        ISystemDbContextFactory factory = GetFactory(options);
        RecordingActionExecutor executor = new();

        AutomationEngineService engine = new(
            factory,
            [executor],
            new LaserGameStatusStore(), TimeProvider.System, NullLogger<AutomationEngineService>.Instance);

        return (new AutomationRuleService(factory, engine), engine, executor);
    }

    private static async Task SeedEngineEnabledAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.AppSettings.Add(new AppSetting
        {
            Id = Guid.NewGuid(),
            Key = EnabledSettingKey,
            Value = "true",
            CreateDate = DateTime.UtcNow
        });

        await ctx.SaveChangesAsync();
    }

    private static AutomationRule BuildRule(
        Guid triggerClientId,
        GameStatus triggerEvent = GameStatus.InGame,
        string name = "Test rule")
    {
        return new AutomationRule
        {
            Id = Guid.NewGuid(),
            Name = name,
            TriggerClientId = triggerClientId,
            TriggerEvent = triggerEvent,
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
        };
    }

    private static async Task SeedRuleDirectlyAsync(
        DbContextOptions<ApplicationDbContext> options, AutomationRule rule)
    {
        await using ApplicationDbContext ctx = new(options);

        rule.IsActive = true;
        rule.IsEnabled = true;
        rule.CreateDate = DateTime.UtcNow;

        ctx.AutomationRules.Add(rule);
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task CreateRuleAsync_ShouldAddRuleToDatabase()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        Guid clientId = Guid.NewGuid();
        AutomationRule rule = BuildRule(clientId, name: "Lights up on game start");

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);

        AutomationRule saved = await ctx.AutomationRules
            .Include(r => r.Actions)
            .SingleAsync();

        Assert.AreEqual("Lights up on game start", saved.Name);
        Assert.AreEqual(clientId, saved.TriggerClientId);
        Assert.AreEqual(GameStatus.InGame, saved.TriggerEvent);
        Assert.HasCount(1, saved.Actions);

        // Set by the service rather than the caller, so a rule always arrives live.
        Assert.IsTrue(saved.IsActive);
        Assert.IsTrue(saved.IsEnabled);
        Assert.AreNotEqual(default, saved.CreateDate);
    }

    [TestMethod]
    public async Task CreateRuleAsync_ShouldInvalidateRuleCache()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);

        (AutomationRuleService service, AutomationEngineService engine, RecordingActionExecutor executor) =
            GetService(options);

        Guid clientId = Guid.NewGuid();
        await SeedRuleDirectlyAsync(options, BuildRule(clientId));

        GameStatusTransitionEvent ev = new(clientId, GameStatus.NotStarted, GameStatus.InGame);

        // Builds the engine's rule cache.
        await engine.ProcessTransitionAsync(ev, CancellationToken.None);
        Assert.HasCount(1, executor.ExecutedActionIds);

        await service.CreateRuleAsync(BuildRule(clientId, name: "Second rule"));

        await engine.ProcessTransitionAsync(ev, CancellationToken.None);

        // 1 from the first pass + 2 from the second. Without the invalidation the engine would
        // still be serving its original one-rule cache and this would be 2 - which is the real
        // symptom: a rule you just created never fires until the process restarts.
        Assert.HasCount(
            3,
            executor.ExecutedActionIds,
            "A newly created rule should fire on the next transition without a restart.");
    }

    [TestMethod]
    public async Task GetRuleAsync_ShouldReturnRule()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid(), name: "Findable");
        await SeedRuleDirectlyAsync(options, rule);

        Result<AutomationRule?> result = await service.GetRuleAsync(rule.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual(rule.Id, result.Data.Id);
        Assert.AreEqual("Findable", result.Data.Name);
        Assert.HasCount(1, result.Data.Actions);
    }

    [TestMethod]
    public async Task GetRuleAsync_ShouldReturnNullWhenNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        Result<AutomationRule?> result = await service.GetRuleAsync(Guid.NewGuid());

        // A missing rule is an empty success, not a failure - the caller distinguishes "no such
        // rule" from "the lookup broke" by IsSuccess.
        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task GetRulesByTriggerAsync_ShouldReturnMatchingRules()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        Guid clientId = Guid.NewGuid();

        await SeedRuleDirectlyAsync(options, BuildRule(clientId, GameStatus.InGame, "match"));
        // Same client, different trigger.
        await SeedRuleDirectlyAsync(options, BuildRule(clientId, GameStatus.NotStarted, "wrong event"));
        // Same trigger, different client.
        await SeedRuleDirectlyAsync(options, BuildRule(Guid.NewGuid(), GameStatus.InGame, "wrong client"));

        Result<IEnumerable<AutomationRule>> result =
            await service.GetRulesByTriggerAsync(clientId, GameStatus.InGame);

        Assert.IsTrue(result.IsSuccess, result.Error);

        List<AutomationRule> rules = result.Data!.ToList();

        Assert.HasCount(1, rules);
        Assert.AreEqual("match", rules[0].Name);
    }

    [TestMethod]
    public async Task GetRulesByTriggerAsync_ShouldNotReturnInactiveRules()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        Guid clientId = Guid.NewGuid();

        AutomationRule softDeleted = BuildRule(clientId, name: "soft deleted");
        AutomationRule disabled = BuildRule(clientId, name: "disabled");

        await SeedRuleDirectlyAsync(options, softDeleted);
        await SeedRuleDirectlyAsync(options, disabled);

        await using (ApplicationDbContext ctx = new(options))
        {
            (await ctx.AutomationRules.FindAsync(softDeleted.Id))!.IsActive = false;
            (await ctx.AutomationRules.FindAsync(disabled.Id))!.IsEnabled = false;
            await ctx.SaveChangesAsync();
        }

        Result<IEnumerable<AutomationRule>> result =
            await service.GetRulesByTriggerAsync(clientId, GameStatus.InGame);

        Assert.IsTrue(result.IsSuccess, result.Error);

        // Both filters apply: IsActive is the soft-delete flag, IsEnabled is the user's on/off
        // toggle, and a rule needs both to be considered for a trigger.
        Assert.IsEmpty(result.Data!);
    }

    [TestMethod]
    public async Task UpdateRuleAsync_ShouldUpdateRule()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid(), name: "Before");
        await SeedRuleDirectlyAsync(options, rule);

        AutomationRule toUpdate = (await service.GetRuleAsync(rule.Id)).Data!;
        toUpdate.Name = "After";
        toUpdate.TriggerEvent = GameStatus.GetReady;

        Result<AutomationRule> result = await service.UpdateRuleAsync(toUpdate);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        AutomationRule saved = await ctx.AutomationRules.SingleAsync();

        Assert.AreEqual("After", saved.Name);
        Assert.AreEqual(GameStatus.GetReady, saved.TriggerEvent);
        Assert.IsNotNull(saved.LastUpdateDate);
    }

    [TestMethod]
    public async Task UpdateRuleAsync_ShouldInvalidateRuleCache()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);

        (AutomationRuleService service, AutomationEngineService engine, RecordingActionExecutor executor) =
            GetService(options);

        Guid clientId = Guid.NewGuid();
        AutomationRule first = BuildRule(clientId);
        await SeedRuleDirectlyAsync(options, first);

        GameStatusTransitionEvent ev = new(clientId, GameStatus.NotStarted, GameStatus.InGame);

        await engine.ProcessTransitionAsync(ev, CancellationToken.None);
        Assert.HasCount(1, executor.ExecutedActionIds);

        // Added behind the engine's back so the cache is stale, then an unrelated update is what
        // has to clear it.
        await SeedRuleDirectlyAsync(options, BuildRule(clientId, name: "Added directly"));

        AutomationRule toUpdate = (await service.GetRuleAsync(first.Id)).Data!;
        toUpdate.Name = "Renamed";

        await service.UpdateRuleAsync(toUpdate);

        await engine.ProcessTransitionAsync(ev, CancellationToken.None);

        Assert.HasCount(
            3,
            executor.ExecutedActionIds,
            "Updating a rule should clear the engine's cache, so the second pass sees both rules.");
    }

    [TestMethod]
    public async Task DeleteRuleAsync_ShouldSoftDeleteRule()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        await SeedRuleDirectlyAsync(options, rule);

        Result<bool> result = await service.DeleteRuleAsync(rule.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Data);

        await using ApplicationDbContext ctx = new(options);

        // Soft delete: the row survives so historical AutomationRuleExecutions still resolve to a
        // rule, it just stops being active.
        AutomationRule saved = await ctx.AutomationRules.SingleAsync();

        Assert.IsFalse(saved.IsActive);
        Assert.IsNotNull(saved.LastUpdateDate);
    }

    [TestMethod]
    public async Task DeleteRuleAsync_ShouldReturnFailureWhenNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        Result<bool> result = await service.DeleteRuleAsync(Guid.NewGuid());

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Rule not found.", result.Error);
    }

    [TestMethod]
    public async Task DeleteRuleAsync_ShouldInvalidateRuleCache()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);

        (AutomationRuleService service, AutomationEngineService engine, RecordingActionExecutor executor) =
            GetService(options);

        Guid clientId = Guid.NewGuid();
        AutomationRule rule = BuildRule(clientId);
        await SeedRuleDirectlyAsync(options, rule);

        GameStatusTransitionEvent ev = new(clientId, GameStatus.NotStarted, GameStatus.InGame);

        await engine.ProcessTransitionAsync(ev, CancellationToken.None);
        Assert.HasCount(1, executor.ExecutedActionIds);

        await service.DeleteRuleAsync(rule.Id);

        await engine.ProcessTransitionAsync(ev, CancellationToken.None);

        // A deleted rule that keeps firing is worse than one that never fires - it keeps driving
        // lights and music from a rule the user believes they removed.
        Assert.HasCount(
            1,
            executor.ExecutedActionIds,
            "A deleted rule must stop firing on the next transition, without a restart.");
    }

    [TestMethod]
    public async Task CreateRule_RejectsAnIdleRuleWithNoThreshold()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.TriggerType = AutomationTriggerType.ClientIdle;
        rule.IdleThresholdMinutes = null;

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsFalse(result.IsSuccess, "An idle rule with no threshold can never fire.");
        Assert.AreEqual("An idle rule needs an idle threshold in minutes.", result.Error);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.AutomationRules.CountAsync());
    }

    [TestMethod]
    public async Task CreateRule_RejectsAnIdleRuleWithANonPositiveThreshold()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.TriggerType = AutomationTriggerType.ClientIdle;
        rule.IdleThresholdMinutes = 0;

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("An idle rule's threshold must be greater than zero minutes.", result.Error);
    }

    [TestMethod]
    public async Task CreateRule_AcceptsAnIdleRuleWithAThreshold()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.TriggerType = AutomationTriggerType.ClientIdle;
        rule.IdleThresholdMinutes = 30;

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        AutomationRule saved = await ctx.AutomationRules.SingleAsync();

        Assert.AreEqual(AutomationTriggerType.ClientIdle, saved.TriggerType);
        Assert.AreEqual(30, saved.IdleThresholdMinutes);
    }

    [TestMethod]
    public async Task CreateRule_LeavesTransitionRulesUnaffectedByIdleValidation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        // A transition rule has no threshold and must stay valid without one.
        AutomationRule rule = BuildRule(Guid.NewGuid());

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        AutomationRule saved = await ctx.AutomationRules.SingleAsync();

        Assert.AreEqual(AutomationTriggerType.GameStatusTransition, saved.TriggerType);
        Assert.IsNull(saved.IdleThresholdMinutes);
    }

    [TestMethod]
    public async Task UpdateRule_RejectsAnIdleRuleWithNoThreshold()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        Result<AutomationRule> createResult = await service.CreateRuleAsync(rule);
        Assert.IsTrue(createResult.IsSuccess, createResult.Error);

        rule.TriggerType = AutomationTriggerType.ClientIdle;
        rule.IdleThresholdMinutes = null;

        Result<AutomationRule> updateResult = await service.UpdateRuleAsync(rule);

        Assert.IsFalse(updateResult.IsSuccess);
        Assert.AreEqual("An idle rule needs an idle threshold in minutes.", updateResult.Error);
    }

    [TestMethod]
    public async Task CreateRule_RejectsAScheduledRuleWithNoTimeOfDay()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.TriggerType = AutomationTriggerType.Scheduled;
        rule.ScheduledTimeOfDay = null;

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsFalse(result.IsSuccess, "A scheduled rule with no time of day can never fire.");
        Assert.AreEqual("A scheduled rule needs a time of day.", result.Error);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.AutomationRules.CountAsync());
    }

    [TestMethod]
    public async Task CreateRule_RejectsAScheduledRuleWithMalformedDaysOfWeek()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.TriggerType = AutomationTriggerType.Scheduled;
        rule.ScheduledTimeOfDay = new TimeOnly(9, 0);
        rule.ScheduledDaysOfWeek = "Monday";

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("A scheduled rule's days of week are not valid.", result.Error);
    }

    [TestMethod]
    public async Task CreateRule_AcceptsAScheduledRuleWithATimeAndNoDays()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.TriggerType = AutomationTriggerType.Scheduled;
        rule.ScheduledTimeOfDay = new TimeOnly(9, 0);
        rule.ScheduledDaysOfWeek = null;

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        AutomationRule saved = await ctx.AutomationRules.SingleAsync();

        Assert.AreEqual(AutomationTriggerType.Scheduled, saved.TriggerType);
        Assert.AreEqual(new TimeOnly(9, 0), saved.ScheduledTimeOfDay);
        Assert.IsNull(saved.ScheduledDaysOfWeek);
    }

    [TestMethod]
    public async Task CreateRule_AcceptsAScheduledRuleWithSpecificDays()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.TriggerType = AutomationTriggerType.Scheduled;
        rule.ScheduledTimeOfDay = new TimeOnly(9, 0);
        rule.ScheduledDaysOfWeek = "1,2,3,4,5";

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        AutomationRule saved = await ctx.AutomationRules.SingleAsync();

        Assert.AreEqual("1,2,3,4,5", saved.ScheduledDaysOfWeek);
    }

    [TestMethod]
    public async Task UpdateRule_RejectsAScheduledRuleWithNoTimeOfDay()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        Result<AutomationRule> createResult = await service.CreateRuleAsync(rule);
        Assert.IsTrue(createResult.IsSuccess, createResult.Error);

        rule.TriggerType = AutomationTriggerType.Scheduled;
        rule.ScheduledTimeOfDay = null;

        Result<AutomationRule> updateResult = await service.UpdateRuleAsync(rule);

        Assert.IsFalse(updateResult.IsSuccess);
        Assert.AreEqual("A scheduled rule needs a time of day.", updateResult.Error);
    }

    [TestMethod]
    public async Task CreateRule_LeavesIdleAndTransitionRulesUnaffectedByScheduleValidation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule idleRule = BuildRule(Guid.NewGuid(), name: "Idle rule");
        idleRule.TriggerType = AutomationTriggerType.ClientIdle;
        idleRule.IdleThresholdMinutes = 30;

        AutomationRule transitionRule = BuildRule(Guid.NewGuid(), name: "Transition rule");

        Result<AutomationRule> idleResult = await service.CreateRuleAsync(idleRule);
        Result<AutomationRule> transitionResult = await service.CreateRuleAsync(transitionRule);

        Assert.IsTrue(idleResult.IsSuccess, idleResult.Error);
        Assert.IsTrue(transitionResult.IsSuccess, transitionResult.Error);
    }

    private static AutomationRuleCondition BuildCondition(AutomationConditionType type, string parametersJson)
    {
        return new AutomationRuleCondition
        {
            Id = Guid.NewGuid(),
            ConditionType = type,
            ParametersJson = parametersJson,
            IsActive = true
        };
    }

    private static AutomationRuleCondition EveningWindow()
    {
        return BuildCondition(
            AutomationConditionType.TimeWindow,
            AutomationConditionParameters.Serialize(new TimeWindowConditionParameters(new TimeOnly(18, 0), new TimeOnly(22, 0))));
    }

    private static async Task<Guid> SeedKioskAsync(DbContextOptions<ApplicationDbContext> options, int companyId)
    {
        await using ApplicationDbContext ctx = new(options);

        Client client = new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = $"Kiosk {companyId}", CreateDate = DateTime.UtcNow };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        return client.Id;
    }

    [TestMethod]
    public async Task CreateRule_SavesItsConditions()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.Conditions = [EveningWindow()];

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsTrue(result.IsSuccess, result.Error);

        AutomationRule saved = (await service.GetRuleAsync(rule.Id)).Data!;
        Assert.HasCount(1, saved.Conditions);
        Assert.AreEqual(AutomationConditionType.TimeWindow, saved.Conditions[0].ConditionType);
    }

    [TestMethod]
    public async Task CreateRule_RejectsATimeWindowThatStartsAndEndsTogether()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.Conditions =
        [
            BuildCondition(
                AutomationConditionType.TimeWindow,
                AutomationConditionParameters.Serialize(new TimeWindowConditionParameters(new TimeOnly(9, 0), new TimeOnly(9, 0))))
        ];

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("A time window's start and end can't be the same time.", result.Error);
    }

    [TestMethod]
    public async Task CreateRule_RejectsAKioskConditionForAKioskThatDoesNotExist()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.Conditions =
        [
            BuildCondition(
                AutomationConditionType.ClientGameStatus,
                AutomationConditionParameters.Serialize(new ClientGameStatusConditionParameters(Guid.NewGuid(), true)))
        ];

        Result<AutomationRule> result = await service.CreateRuleAsync(rule);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("A game status condition refers to a kiosk that doesn't exist.", result.Error);
    }

    [TestMethod]
    public async Task CreateRule_RejectsAKioskConditionForAnotherCompanysKiosk()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Guid ownKioskId = await SeedKioskAsync(options, companyId: 1);
        Guid otherKioskId = await SeedKioskAsync(options, companyId: 2);

        // A signed-in user of company 1: the same tenant-filtered factory the app gives the service.
        Mock<IDbContextFactory<ApplicationDbContext>> tenantFactory = new();
        tenantFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options, new FixedTenantProvider(1)));

        AutomationEngineService engine = new(GetFactory(options), [], new LaserGameStatusStore(), TimeProvider.System, NullLogger<AutomationEngineService>.Instance);
        AutomationRuleService service = new(tenantFactory.Object, engine);

        AutomationRule foreign = BuildRule(ownKioskId);
        foreign.Conditions =
        [
            BuildCondition(
                AutomationConditionType.ClientGameStatus,
                AutomationConditionParameters.Serialize(new ClientGameStatusConditionParameters(otherKioskId, true)))
        ];

        AutomationRule own = BuildRule(ownKioskId);
        own.Conditions =
        [
            BuildCondition(
                AutomationConditionType.ClientGameStatus,
                AutomationConditionParameters.Serialize(new ClientGameStatusConditionParameters(ownKioskId, true)))
        ];

        Result<AutomationRule> foreignResult = await service.CreateRuleAsync(foreign);
        Result<AutomationRule> ownResult = await service.CreateRuleAsync(own);

        Assert.IsFalse(foreignResult.IsSuccess, "Another company's kiosk must look like it doesn't exist.");
        Assert.IsTrue(ownResult.IsSuccess, ownResult.Error);
    }

    [TestMethod]
    public async Task UpdateRule_AddsAnActionAddedWhileEditing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        await SeedRuleDirectlyAsync(options, rule);

        // What the dialog sends: the existing action plus a new one with a client-side Guid.
        AutomationRule edited = (await service.GetRuleAsync(rule.Id)).Data!;
        edited.Actions.Add(new AutomationRuleAction
        {
            Id = Guid.NewGuid(),
            ActionType = RecordingActionExecutor.TestActionType,
            ParametersJson = "{\"Second\":true}",
            SortOrder = 1,
            IsActive = true
        });

        Result<AutomationRule> result = await service.UpdateRuleAsync(edited);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.HasCount(2, (await service.GetRuleAsync(rule.Id)).Data!.Actions);
    }

    [TestMethod]
    public async Task UpdateRule_SoftDeletesRemovedActionsAndConditions()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.Actions.Add(new AutomationRuleAction
        {
            Id = Guid.NewGuid(),
            ActionType = RecordingActionExecutor.TestActionType,
            ParametersJson = "{}",
            SortOrder = 1,
            IsActive = true
        });
        rule.Conditions = [EveningWindow()];
        await SeedRuleDirectlyAsync(options, rule);

        AutomationRule edited = (await service.GetRuleAsync(rule.Id)).Data!;
        Guid keptActionId = edited.Actions.OrderBy(a => a.SortOrder).First().Id;
        edited.Actions = [edited.Actions.Single(a => a.Id == keptActionId)];
        edited.Conditions = [];

        Result<AutomationRule> result = await service.UpdateRuleAsync(edited);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        List<AutomationRuleAction> actions = await ctx.AutomationRuleActions.ToListAsync();
        List<AutomationRuleCondition> conditions = await ctx.AutomationRuleConditions.ToListAsync();

        Assert.HasCount(2, actions, "Removed rows are soft-deleted, not hard-deleted.");
        Assert.IsTrue(actions.Single(a => a.Id == keptActionId).IsActive);
        Assert.IsFalse(actions.Single(a => a.Id != keptActionId).IsActive);
        Assert.IsFalse(conditions.Single().IsActive);
    }

    [TestMethod]
    public async Task UpdateRule_SavesAnEditedCondition()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        rule.Conditions = [EveningWindow()];
        await SeedRuleDirectlyAsync(options, rule);

        AutomationRule edited = (await service.GetRuleAsync(rule.Id)).Data!;
        string lateWindow = AutomationConditionParameters.Serialize(new TimeWindowConditionParameters(new TimeOnly(22, 0), new TimeOnly(2, 0)));
        edited.Conditions[0].ParametersJson = lateWindow;

        Result<AutomationRule> result = await service.UpdateRuleAsync(edited);

        Assert.IsTrue(result.IsSuccess, result.Error);

        AutomationRuleCondition saved = (await service.GetRuleAsync(rule.Id)).Data!.Conditions.Single();
        Assert.AreEqual(rule.Conditions[0].Id, saved.Id, "An edited condition keeps its row rather than being replaced.");
        Assert.AreEqual(lateWindow, saved.ParametersJson);
    }

    [TestMethod]
    public async Task UpdateRule_KeepsADisabledRuleDisabled()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        await SeedRuleDirectlyAsync(options, rule);
        await service.SetRuleEnabledAsync(rule.Id, false);

        // The dialog builds a fresh AutomationRule, whose IsEnabled defaults to true.
        AutomationRule edited = (await service.GetRuleAsync(rule.Id)).Data!;
        edited.IsEnabled = true;
        edited.Name = "Renamed";

        Result<AutomationRule> result = await service.UpdateRuleAsync(edited);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse((await service.GetRuleAsync(rule.Id)).Data!.IsEnabled, "Editing a rule must not switch it back on.");
    }

    [TestMethod]
    public async Task UpdateRule_FailsForARuleThatDoesNotExist()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        Result<AutomationRule> result = await service.UpdateRuleAsync(BuildRule(Guid.NewGuid()));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Rule not found.", result.Error);
    }

    private static AutomationRuleCondition WhileOpen(int locationId)
    {
        return BuildCondition(
            AutomationConditionType.DuringOpeningHours,
            AutomationConditionParameters.Serialize(new OpeningHoursConditionParameters(locationId, true)));
    }

    private static async Task<int> SeedLocationAsync(DbContextOptions<ApplicationDbContext> options, int companyId, bool isActive = true)
    {
        await using ApplicationDbContext ctx = new(options);

        Location location = new() { CompanyId = companyId, Name = $"Location {companyId}", IsActive = isActive, CreateDate = DateTime.UtcNow, UpdateDate = DateTime.UtcNow };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        return location.Id;
    }

    [TestMethod]
    public async Task CreateRule_RejectsAnOpeningHoursConditionForALocationThatDoesNotExist()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int deactivatedId = await SeedLocationAsync(options, companyId: 1, isActive: false);
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule missing = BuildRule(Guid.NewGuid());
        missing.Conditions = [WhileOpen(999)];
        AutomationRule deactivated = BuildRule(Guid.NewGuid());
        deactivated.Conditions = [WhileOpen(deactivatedId)];

        Result<AutomationRule> missingResult = await service.CreateRuleAsync(missing);
        Result<AutomationRule> deactivatedResult = await service.CreateRuleAsync(deactivated);

        Assert.AreEqual("An opening hours condition refers to a location that doesn't exist.", missingResult.Error);
        Assert.IsFalse(deactivatedResult.IsSuccess);
    }

    [TestMethod]
    public async Task CreateRule_RejectsAnOpeningHoursConditionForAnotherCompanysLocation()
    {
        // Locations aren't tenant-filtered, so this is an explicit check rather than the filter.
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int ownLocationId = await SeedLocationAsync(options, companyId: 1);
        int otherLocationId = await SeedLocationAsync(options, companyId: 2);

        Mock<IDbContextFactory<ApplicationDbContext>> tenantFactory = new();
        tenantFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options, new FixedTenantProvider(1)));

        AutomationEngineService engine = new(GetFactory(options), [], new LaserGameStatusStore(), TimeProvider.System, NullLogger<AutomationEngineService>.Instance);
        AutomationRuleService service = new(tenantFactory.Object, engine);

        AutomationRule foreign = BuildRule(Guid.NewGuid());
        foreign.Conditions = [WhileOpen(otherLocationId)];
        AutomationRule own = BuildRule(Guid.NewGuid());
        own.Conditions = [WhileOpen(ownLocationId)];

        Result<AutomationRule> foreignResult = await service.CreateRuleAsync(foreign);
        Result<AutomationRule> ownResult = await service.CreateRuleAsync(own);

        Assert.IsFalse(foreignResult.IsSuccess);
        Assert.IsTrue(ownResult.IsSuccess, ownResult.Error);
    }

    [TestMethod]
    public async Task CreateRule_ValidatesTheGameDataTriggers()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule noSeconds = BuildRule(Guid.NewGuid());
        noSeconds.TriggerType = AutomationTriggerType.TimeRemainingBelow;

        AutomationRule noScope = BuildRule(Guid.NewGuid());
        noScope.TriggerType = AutomationTriggerType.ScoreThreshold;
        noScope.TriggerThreshold = 1000;

        AutomationRule good = BuildRule(Guid.NewGuid());
        good.TriggerType = AutomationTriggerType.ScoreThreshold;
        good.TriggerThreshold = 1000;
        good.TriggerScoreScope = AutomationScoreScope.Team;
        good.TriggerTeam = Team.Red;

        Assert.AreEqual("A time remaining rule needs a number of seconds greater than zero.", (await service.CreateRuleAsync(noSeconds)).Error);
        Assert.AreEqual("A score rule needs to say whose score counts.", (await service.CreateRuleAsync(noScope)).Error);

        Result<AutomationRule> goodResult = await service.CreateRuleAsync(good);
        Assert.IsTrue(goodResult.IsSuccess, goodResult.Error);
    }

    [TestMethod]
    public async Task UpdateRule_SavesTheGameDataTriggerFields()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (AutomationRuleService service, _, _) = GetService(options);

        AutomationRule rule = BuildRule(Guid.NewGuid());
        await SeedRuleDirectlyAsync(options, rule);

        AutomationRule edited = (await service.GetRuleAsync(rule.Id)).Data!;
        edited.TriggerType = AutomationTriggerType.TimeRemainingBelow;
        edited.TriggerThreshold = 45;

        Result<AutomationRule> result = await service.UpdateRuleAsync(edited);

        Assert.IsTrue(result.IsSuccess, result.Error);
        AutomationRule saved = (await service.GetRuleAsync(rule.Id)).Data!;
        Assert.AreEqual(AutomationTriggerType.TimeRemainingBelow, saved.TriggerType);
        Assert.AreEqual(45, saved.TriggerThreshold);
    }

    [TestMethod]
    public void TriggerType_ExistingValuesNeverMove()
    {
        // Stored as ints: every rule saved before these triggers must still mean the same thing.
        Assert.AreEqual(0, (int)AutomationTriggerType.GameStatusTransition);
        Assert.AreEqual(1, (int)AutomationTriggerType.ClientIdle);
        Assert.AreEqual(2, (int)AutomationTriggerType.Scheduled);
    }
}

