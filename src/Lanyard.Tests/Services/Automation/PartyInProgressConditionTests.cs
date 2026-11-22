#nullable enable

using Lanyard.Application.Services;
using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

/// <summary>
/// The "a party is or isn't on" condition: the pure evaluator, then the engine wiring it to the
/// party schedule, then the rule service's save checks.
/// </summary>
[TestClass]
public class PartyInProgressConditionTests
{
    private const int CompanyId = 1;
    private const int LocationId = 7;
    private static readonly DateTime Now = new(2026, 11, 21, 14, 0, 0, DateTimeKind.Utc);
    private static readonly AutomationLocationInfo Ipswich = new(CompanyId, "Ipswich", []);

    private static PartySlot Party(DateTime start, DateTime end, string? room = "Room 2") =>
        new(Guid.NewGuid(), LocationId, start, end, null, null, room, "Ava", 7, DateTime.UtcNow);

    private static AutomationRuleCondition Condition(bool inProgress, string? room = null, int locationId = LocationId) => new()
    {
        Id = Guid.NewGuid(),
        ConditionType = AutomationConditionType.PartyInProgress,
        ParametersJson = AutomationConditionParameters.Serialize(new PartyInProgressConditionParameters(locationId, room, inProgress)),
        IsActive = true
    };

    private static AutomationConditionCheck Evaluate(AutomationRuleCondition condition, IReadOnlyList<PartySlot>? parties, AutomationLocationInfo? location = null, DateTime? now = null) =>
        AutomationConditionEvaluator.Evaluate(
            [condition], CompanyId, now ?? Now,
            _ => null, _ => false,
            id => id == LocationId ? location ?? Ipswich : null,
            id => id == LocationId ? parties : null);

    [TestMethod]
    public void APartyOn_PassesOnlyFromItsStartUntilItsEnd()
    {
        PartySlot party = Party(Now, Now.AddHours(2));

        Assert.IsTrue(Evaluate(Condition(true), [party], now: Now).Passed, "Start is inclusive.");
        Assert.IsTrue(Evaluate(Condition(true), [party], now: Now.AddMinutes(119)).Passed);
        Assert.IsFalse(Evaluate(Condition(true), [party], now: Now.AddHours(2)).Passed, "End is exclusive.");
        Assert.IsFalse(Evaluate(Condition(true), [party], now: Now.AddMinutes(-1)).Passed);
    }

    [TestMethod]
    public void NoPartyOn_IsTheOpposite()
    {
        PartySlot party = Party(Now.AddHours(-1), Now.AddHours(1));

        AutomationConditionCheck during = Evaluate(Condition(false), [party]);
        Assert.IsFalse(during.Passed);
        Assert.AreEqual("A party is on in Ipswich", during.Reason);
        Assert.IsFalse(during.IsMisconfigured);

        Assert.IsTrue(Evaluate(Condition(false), []).Passed);
    }

    [TestMethod]
    public void ARoom_OnlyCountsPartiesInThatRoom()
    {
        PartySlot party = Party(Now.AddHours(-1), Now.AddHours(1), room: "Room 2");

        Assert.IsTrue(Evaluate(Condition(true, " room 2 "), [party]).Passed);

        AutomationConditionCheck otherRoom = Evaluate(Condition(true, "Room 1"), [party]);
        Assert.IsFalse(otherRoom.Passed);
        Assert.AreEqual("No party is on in Room 1 at Ipswich", otherRoom.Reason);
    }

    [TestMethod]
    public void PartiesThatCouldntBeLoaded_FailClosed_ButArentMisconfigured()
    {
        AutomationConditionCheck check = Evaluate(Condition(false), parties: null);

        Assert.IsFalse(check.Passed);
        Assert.IsFalse(check.IsMisconfigured);
        StringAssert.Contains(check.Reason, "couldn't be loaded");
    }

    [TestMethod]
    public void AMissingLocation_AnotherCompanysLocation_OrBadSettings_AreMisconfigured()
    {
        Assert.IsTrue(Evaluate(Condition(true, locationId: 99), []).IsMisconfigured);
        Assert.IsTrue(Evaluate(Condition(true), [], location: new AutomationLocationInfo(2, "Elsewhere", [])).IsMisconfigured);

        AutomationRuleCondition broken = Condition(true);
        broken.ParametersJson = "{not json";
        Assert.IsTrue(Evaluate(broken, []).IsMisconfigured);
    }

    [TestMethod]
    public void Validate_NeedsALocation_AndAShortRoom()
    {
        Assert.IsNull(AutomationConditionParameters.Validate(AutomationConditionType.PartyInProgress, Condition(true, "Room 2").ParametersJson));
        Assert.IsNotNull(AutomationConditionParameters.Validate(AutomationConditionType.PartyInProgress, Condition(true, locationId: 0).ParametersJson));
        Assert.IsNotNull(AutomationConditionParameters.Validate(AutomationConditionType.PartyInProgress, Condition(true, new string('x', 101)).ParametersJson));
    }

    // --- Engine wiring ---

    private sealed class FakeSchedule(IReadOnlyList<PartySlot> parties) : IPartyScheduleProvider
    {
        public bool Fail { get; set; }
        public int Reads { get; private set; }

        public Task<Result<IReadOnlyList<PartySlot>>> GetDayAsync(int companyId, int locationId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Fail
                ? Result<IReadOnlyList<PartySlot>>.Fail("down")
                : Result<IReadOnlyList<PartySlot>>.Ok(companyId == CompanyId && locationId == LocationId ? parties : []));
        }

        public void Invalidate(int locationId)
        {
        }
    }

    private static DbContextOptions<ApplicationDbContext> Options() =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static ISystemDbContextFactory Factory(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<ISystemDbContextFactory> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));
        return factory.Object;
    }

    // A Game Status Change rule on a kiosk, only if a party is (or isn't) on at the location.
    private static async Task<(AutomationEngineService Engine, RecordingActionExecutor Executor, Guid KioskId)> SetUpAsync(
        DbContextOptions<ApplicationDbContext> options, IPartyScheduleProvider schedule, bool whenPartyOn)
    {
        Guid kioskId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = "AutomationEngine.Enabled", Value = "true" });
            ctx.Locations.Add(new Location { Id = LocationId, CompanyId = CompanyId, Name = "Ipswich", IsActive = true });
            ctx.AutomationRules.Add(new AutomationRule
            {
                Id = Guid.NewGuid(),
                CompanyId = CompanyId,
                Name = "Party game lights",
                TriggerClientId = kioskId,
                TriggerType = AutomationTriggerType.GameStatusTransition,
                TriggerEvent = GameStatus.InGame,
                IsActive = true,
                IsEnabled = true,
                CreateDate = DateTime.UtcNow,
                Actions = [new AutomationRuleAction { Id = Guid.NewGuid(), ActionType = RecordingActionExecutor.TestActionType, ParametersJson = "{}", IsActive = true }],
                Conditions = [Condition(whenPartyOn)]
            });
            await ctx.SaveChangesAsync();
        }

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = new(
            Factory(options), [executor], new LaserGameStatusStore(), new Lanyard.Tests.Services.OperationsAlerts.RecordingOperationsAlertWriter(),
            TimeProvider.System, NullLogger<AutomationEngineService>.Instance, schedule);

        return (engine, executor, kioskId);
    }

    private static async Task<List<AutomationRuleExecution>> ExecutionsAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);
        return await ctx.AutomationRuleExecutions.ToListAsync();
    }

    [TestMethod]
    public async Task Engine_RunsTheRuleWhileAPartyIsOn()
    {
        DbContextOptions<ApplicationDbContext> options = Options();
        FakeSchedule schedule = new([Party(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1))]);
        (AutomationEngineService engine, RecordingActionExecutor executor, Guid kioskId) = await SetUpAsync(options, schedule, whenPartyOn: true);

        await engine.ProcessTransitionAsync(new GameStatusTransitionEvent(kioskId, GameStatus.NotStarted, GameStatus.InGame), CancellationToken.None);

        Assert.HasCount(1, executor.ExecutedActionIds);
        Assert.AreEqual(1, schedule.Reads);
    }

    [TestMethod]
    public async Task Engine_SkipsTheRuleWhenNoPartyIsOn_AndSaysWhy()
    {
        DbContextOptions<ApplicationDbContext> options = Options();
        FakeSchedule schedule = new([Party(DateTime.UtcNow.AddHours(2), DateTime.UtcNow.AddHours(4))]);
        (AutomationEngineService engine, RecordingActionExecutor executor, Guid kioskId) = await SetUpAsync(options, schedule, whenPartyOn: true);

        await engine.ProcessTransitionAsync(new GameStatusTransitionEvent(kioskId, GameStatus.NotStarted, GameStatus.InGame), CancellationToken.None);

        Assert.IsEmpty(executor.ExecutedActionIds);
        AutomationRuleExecution skipped = (await ExecutionsAsync(options)).Single();
        Assert.AreEqual("No party is on in Ipswich", skipped.SkipReason);
    }

    [TestMethod]
    public async Task Engine_FailsClosed_WhenPartiesCantBeRead()
    {
        DbContextOptions<ApplicationDbContext> options = Options();
        FakeSchedule schedule = new([]) { Fail = true };
        (AutomationEngineService engine, RecordingActionExecutor executor, Guid kioskId) = await SetUpAsync(options, schedule, whenPartyOn: false);

        await engine.ProcessTransitionAsync(new GameStatusTransitionEvent(kioskId, GameStatus.NotStarted, GameStatus.InGame), CancellationToken.None);

        Assert.IsEmpty(executor.ExecutedActionIds, "\"No party is on\" mustn't pass just because the parties couldn't be read.");
        StringAssert.Contains((await ExecutionsAsync(options)).Single().SkipReason, "couldn't be loaded");
    }

    [TestMethod]
    public async Task Engine_DoesntReadParties_ForRulesWithoutAPartyCondition()
    {
        DbContextOptions<ApplicationDbContext> options = Options();
        FakeSchedule schedule = new([]);
        (AutomationEngineService engine, _, Guid kioskId) = await SetUpAsync(options, schedule, whenPartyOn: true);

        await using (ApplicationDbContext ctx = new(options))
        {
            AutomationRuleCondition condition = await ctx.AutomationRuleConditions.SingleAsync();
            condition.IsActive = false;
            await ctx.SaveChangesAsync();
        }

        engine.InvalidateRuleCache();
        await engine.ProcessTransitionAsync(new GameStatusTransitionEvent(kioskId, GameStatus.NotStarted, GameStatus.InGame), CancellationToken.None);

        Assert.AreEqual(0, schedule.Reads);
    }

    // --- Rule service ---

    [TestMethod]
    public async Task RuleService_RejectsAPartyConditionForAnotherCompanysLocation()
    {
        DbContextOptions<ApplicationDbContext> options = Options();
        Guid kioskId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Locations.Add(new Location { Id = LocationId, CompanyId = CompanyId, Name = "Ipswich", IsActive = true });
            ctx.Locations.Add(new Location { Id = 8, CompanyId = 2, Name = "Elsewhere", IsActive = true });
            ctx.Clients.Add(new Client { Id = kioskId, CompanyId = CompanyId, Name = "Kiosk", CreateDate = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        Mock<IDbContextFactory<ApplicationDbContext>> tenantFactory = new();
        tenantFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options, new FixedTenantProvider(CompanyId)));
        AutomationEngineService engine = new(Factory(options), [], new LaserGameStatusStore(), new Lanyard.Tests.Services.OperationsAlerts.RecordingOperationsAlertWriter(), TimeProvider.System, NullLogger<AutomationEngineService>.Instance, new NoPartiesSchedule());
        AutomationRuleService service = new(tenantFactory.Object, engine);

        AutomationRule Rule(int locationId) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Party lights",
            TriggerClientId = kioskId,
            TriggerEvent = GameStatus.InGame,
            Actions = [new AutomationRuleAction { Id = Guid.NewGuid(), ActionType = RecordingActionExecutor.TestActionType, ParametersJson = "{}", IsActive = true }],
            Conditions = [Condition(true, "Room 2", locationId)]
        };

        Result<AutomationRule> foreign = await service.CreateRuleAsync(Rule(8));
        Result<AutomationRule> missing = await service.CreateRuleAsync(Rule(99));
        Result<AutomationRule> own = await service.CreateRuleAsync(Rule(LocationId));

        Assert.AreEqual("A party condition refers to a location that doesn't exist.", foreign.Error);
        Assert.AreEqual("A party condition refers to a location that doesn't exist.", missing.Error);
        Assert.IsTrue(own.IsSuccess, own.Error);
    }
}
