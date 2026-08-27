#nullable enable

using Lanyard.Application.Services;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DataAccess;
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

    private static IDbContextFactory<ApplicationDbContext> GetFactory(
        DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();

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
        IDbContextFactory<ApplicationDbContext> factory = GetFactory(options);
        RecordingActionExecutor executor = new();

        AutomationEngineService engine = new(
            factory,
            [executor],
            NullLogger<AutomationEngineService>.Instance);

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
}
