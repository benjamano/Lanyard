#nullable enable

using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Lanyard.Shared.Enum;
using Lanyard.Tests.Services.OperationsAlerts;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Automation;

/// <summary>
/// "Run now (test)": TestRunRuleAsync runs a rule's actions once, logs it as a test, and must
/// neither escape the rule's company nor change when the rule next fires for real.
/// </summary>
[TestClass]
public class AutomationEngineServiceTestRunTests
{
    private const int OwnCompany = ApplicationDbContext.SeedPlay2DayCompanyId;
    private const int OtherCompany = OwnCompany + 100;
    private const string UserId = "user-1";

    // 07:00 UTC on Wednesday 15 July 2026 is 08:00 venue time (BST), before a 09:00 schedule.
    private static readonly DateTime SummerMorningUtc = new(2026, 7, 15, 7, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        DbContextOptions<ApplicationDbContext> Options,
        ISystemDbContextFactory Factory,
        AutomationEngineService Engine,
        RecordingActionExecutor Executor,
        AnyTypeExecutor AnyType,
        RecordingOperationsAlertWriter Alerts,
        TestClock Clock);

    // Handles every real action type and records what reached it, so the company check can be
    // tested for each type without driving real kiosks.
    private sealed class AnyTypeExecutor : IActionExecutor
    {
        public List<Guid> ExecutedActionIds { get; } = [];

        public bool CanHandle(string actionType) => AutomationActionTargets.IsKnown(actionType);

        public Task<(bool Success, string? ErrorMessage)> ExecuteAsync(AutomationRuleAction action, Guid triggerClientId)
        {
            ExecutedActionIds.Add(action.Id);
            return Task.FromResult((true, (string?)null));
        }
    }

    private static async Task<Harness> CreateHarnessAsync(
        bool engineEnabled = true,
        Func<AutomationRuleAction, (bool Success, string? ErrorMessage)>? behaviour = null,
        DbContextOptions<ApplicationDbContext>? options = null)
    {
        options ??= new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        Mock<ISystemDbContextFactory> factoryMock = new();
        DbContextOptions<ApplicationDbContext> captured = options;
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(captured));

        await using (ApplicationDbContext ctx = new(options))
        {
            if (!await ctx.AppSettings.AnyAsync(s => s.Key == "AutomationEngine.Enabled"))
            {
                ctx.AppSettings.Add(new AppSetting
                {
                    Id = Guid.NewGuid(),
                    Key = "AutomationEngine.Enabled",
                    Value = engineEnabled ? "true" : "false",
                    CreateDate = DateTime.UtcNow
                });

                await ctx.SaveChangesAsync();
            }
        }

        RecordingActionExecutor executor = new(behaviour);
        AnyTypeExecutor anyType = new();
        RecordingOperationsAlertWriter alerts = new();
        TestClock clock = new(SummerMorningUtc);

        AutomationEngineService engine = new(
            factoryMock.Object,
            [executor, anyType],
            new LaserGameStatusStore(),
            alerts,
            clock,
            NullLogger<AutomationEngineService>.Instance,
            new NoPartiesSchedule());

        return new Harness(options, factoryMock.Object, engine, executor, anyType, alerts, clock);
    }

    private static async Task<Guid> SeedKioskAsync(Harness harness, int companyId = OwnCompany)
    {
        await using ApplicationDbContext ctx = new(harness.Options);

        Client client = new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = $"Kiosk {companyId}", CreateDate = DateTime.UtcNow };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        return client.Id;
    }

    private static AutomationRuleAction TestAction(int sortOrder = 0) => new()
    {
        Id = Guid.NewGuid(),
        ActionType = RecordingActionExecutor.TestActionType,
        ParametersJson = "{}",
        SortOrder = sortOrder,
        IsActive = true
    };

    private static async Task<AutomationRule> SeedRuleAsync(
        Harness harness,
        Guid triggerClientId,
        IEnumerable<AutomationRuleAction> actions,
        AutomationTriggerType triggerType = AutomationTriggerType.GameStatusTransition,
        bool isEnabled = true,
        bool isActive = true,
        params AutomationRuleCondition[] conditions)
    {
        await using ApplicationDbContext ctx = new(harness.Options);

        AutomationRule rule = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = OwnCompany,
            Name = "Winner lights",
            TriggerClientId = triggerClientId,
            TriggerType = triggerType,
            TriggerEvent = GameStatus.InGame,
            IdleThresholdMinutes = triggerType == AutomationTriggerType.ClientIdle ? 30 : null,
            ScheduledTimeOfDay = triggerType == AutomationTriggerType.Scheduled ? new TimeOnly(9, 0) : null,
            IsActive = isActive,
            IsEnabled = isEnabled,
            CreateDate = DateTime.UtcNow,
            Actions = actions.ToList(),
            Conditions = conditions.ToList()
        };

        ctx.AutomationRules.Add(rule);
        await ctx.SaveChangesAsync();

        return rule;
    }

    private static AutomationTestRunRequest Request(Guid ruleId, int companyId = OwnCompany, bool checkConditions = false) =>
        new(ruleId, companyId, UserId, "Sam Manager", checkConditions);

    private static async Task<List<AutomationRuleExecution>> GetExecutionsAsync(Harness harness)
    {
        await using ApplicationDbContext ctx = new(harness.Options);

        return await ctx.AutomationRuleExecutions.Include(e => e.ActionExecutions).ToListAsync();
    }

    [TestMethod]
    public async Task TestRun_RunsEveryActionInOrder_AndLogsItAsATest()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRuleAction second = TestAction(1);
        AutomationRuleAction first = TestAction(0);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [second, first]);

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Data!.AllPassed);
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, harness.Executor.ExecutedActionIds);
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, result.Data.Actions.Select(a => a.ActionId).ToArray());

        AutomationRuleExecution execution = (await GetExecutionsAsync(harness)).Single();
        Assert.AreEqual(result.Data.ExecutionId, execution.Id);
        Assert.IsTrue(execution.IsTestRun);
        Assert.AreEqual(UserId, execution.TestRunByUserId);
        Assert.AreEqual("Manual test by Sam Manager", execution.TriggerEvent);
        Assert.AreEqual(kioskId, execution.TriggerClientId);
        Assert.IsTrue(execution.OverallSuccess);
        Assert.HasCount(2, execution.ActionExecutions);
    }

    [TestMethod]
    public async Task TestRun_ReportsAFailedAndAThrowingAction_AndStillRunsTheRest()
    {
        AutomationRuleAction failing = TestAction(0);
        AutomationRuleAction throwing = TestAction(1);
        AutomationRuleAction fine = TestAction(2);

        Harness harness = await CreateHarnessAsync(behaviour: action =>
            action.Id == failing.Id ? (false, "Client not connected")
            : action.Id == throwing.Id ? throw new InvalidOperationException("boom")
            : (true, null));

        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [failing, throwing, fine]);

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse(result.Data!.AllPassed);
        Assert.AreEqual("Client not connected", result.Data.Actions[0].ErrorMessage);
        StringAssert.Contains(result.Data.Actions[1].ErrorMessage, "boom");
        Assert.IsTrue(result.Data.Actions[2].Success);
        Assert.IsFalse((await GetExecutionsAsync(harness)).Single().OverallSuccess);
    }

    [TestMethod]
    public async Task TestRun_RefusesAnotherCompanysRule_AndRunsNothing()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [TestAction()]);

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id, companyId: OtherCompany));

        Assert.AreEqual("Rule not found.", result.Error);
        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
        Assert.IsEmpty(await GetExecutionsAsync(harness));
    }

    [TestMethod]
    public async Task TestRun_RefusesADeletedOrMissingRule()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRule deleted = await SeedRuleAsync(harness, kioskId, [TestAction()], isActive: false);

        Assert.AreEqual("Rule not found.", (await harness.Engine.TestRunRuleAsync(Request(deleted.Id))).Error);
        Assert.AreEqual("Rule not found.", (await harness.Engine.TestRunRuleAsync(Request(Guid.NewGuid()))).Error);
        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task TestRun_RefusesASwitchedOffRule()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [TestAction()], isEnabled: false);

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id));

        Assert.AreEqual("Switch the rule on before testing it.", result.Error);
        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
        Assert.IsEmpty(await GetExecutionsAsync(harness));
    }

    [TestMethod]
    public async Task TestRun_WorksWhileTheEngineIsSwitchedOff()
    {
        Harness harness = await CreateHarnessAsync(engineEnabled: false);
        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [TestAction()]);

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
    }

    private static AutomationRuleCondition NightOnly() => new()
    {
        Id = Guid.NewGuid(),
        ConditionType = AutomationConditionType.TimeWindow,
        ParametersJson = AutomationConditionParameters.Serialize(new TimeWindowConditionParameters(new TimeOnly(22, 0), new TimeOnly(23, 0))),
        IsActive = true
    };

    [TestMethod]
    public async Task TestRun_IgnoresConditions_UnlessAskedToCheckThem()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);

        // 08:00 venue time is outside 22:00-23:00, so a real trigger would skip this rule.
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [TestAction()], conditions: NightOnly());

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse(result.Data!.Skipped);
        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task TestRun_CheckingConditions_SkipsAndSaysWhy()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [TestAction()], conditions: NightOnly());

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id, checkConditions: true));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Data!.Skipped);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Data.SkipReason));
        Assert.IsEmpty(result.Data.Actions);
        Assert.IsEmpty(harness.Executor.ExecutedActionIds);

        AutomationRuleExecution execution = (await GetExecutionsAsync(harness)).Single();
        Assert.IsTrue(execution.IsTestRun);
        Assert.AreEqual(result.Data.SkipReason, execution.SkipReason);
    }

    // A location open 07:00-20:00 on Wednesdays, with Staff Scheduling on or off.
    private static async Task<Location> SeedOpenLocationAsync(Harness harness, bool staffScheduling = true)
    {
        await using ApplicationDbContext ctx = new(harness.Options);

        Location location = new() { CompanyId = OwnCompany, Name = "Ipswich", IsActive = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        ctx.LocationOpeningHours.Add(new LocationOpeningHours
        {
            LocationId = location.Id,
            DayOfWeek = DayOfWeek.Wednesday,
            OpensAt = new TimeOnly(7, 0),
            ClosesAt = new TimeOnly(20, 0),
            IsActive = true
        });

        if (staffScheduling)
        {
            ctx.LocationFeatureSettings.Add(new LocationFeatureSetting { LocationId = location.Id, Feature = CompanyFeature.StaffScheduling, IsEnabled = true });
        }

        ctx.RotaHolidays.Add(new RotaHoliday
        {
            Id = Guid.NewGuid(),
            CompanyId = OwnCompany,
            LocationId = location.Id,
            Name = "Summer refit",
            Kind = RotaHolidayKind.Closed,
            StartDate = DateOnly.FromDateTime(SummerMorningUtc),
            EndDate = DateOnly.FromDateTime(SummerMorningUtc)
        });

        await ctx.SaveChangesAsync();

        return location;
    }

    private static AutomationRuleCondition WhileOpen(int locationId) => new()
    {
        Id = Guid.NewGuid(),
        ConditionType = AutomationConditionType.DuringOpeningHours,
        ParametersJson = AutomationConditionParameters.Serialize(new OpeningHoursConditionParameters(locationId, true)),
        IsActive = true
    };

    [TestMethod]
    public async Task TestRun_CheckingConditions_OnAClosedDay_SaysTheLocationIsClosedForTheHoliday()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);
        Location ipswich = await SeedOpenLocationAsync(harness);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [TestAction()], conditions: WhileOpen(ipswich.Id));

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id, checkConditions: true));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Data!.Skipped);
        Assert.AreEqual("Ipswich is closed today for Summer refit (Rota › Holidays)", result.Data.SkipReason);
        Assert.IsEmpty(harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task TestRun_CheckingConditions_IgnoresHolidaysWhereStaffSchedulingIsOff()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);
        Location ipswich = await SeedOpenLocationAsync(harness, staffScheduling: false);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [TestAction()], conditions: WhileOpen(ipswich.Id));

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id, checkConditions: true));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse(result.Data!.Skipped, result.Data.SkipReason);
        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task TestRun_CheckingConditions_RunsWhenTheyHold()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [TestAction()], conditions: NightOnly());

        // 21:30 UTC is 22:30 venue time.
        harness.Clock.UtcNow = new DateTime(2026, 7, 15, 21, 30, 0, DateTimeKind.Utc);

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id, checkConditions: true));

        Assert.IsFalse(result.Data!.Skipped);
        Assert.HasCount(1, harness.Executor.ExecutedActionIds);
    }

    // What each action type's parameters look like, aimed at a kiosk (and, where the type names
    // one, a playlist/program/scene/clip).
    private static string ParametersFor(string actionType, Guid clientId, Guid referenceId) => actionType switch
    {
        AutomationActionTypes.MusicControl => $"{{\"TargetClientId\":\"{clientId}\",\"Operation\":\"Play\",\"PlaylistId\":\"{referenceId}\"}}",
        AutomationActionTypes.StartProjectionProgram => $"{{\"TargetClientId\":\"{clientId}\",\"ProjectionProgramId\":\"{referenceId}\"}}",
        AutomationActionTypes.StopProjectionProgram => $"{{\"TargetClientId\":\"{clientId}\"}}",
        AutomationActionTypes.ProjectionProgramControl => $"{{\"TargetClientId\":\"{clientId}\",\"Operation\":\"Start\",\"ProjectionProgramId\":\"{referenceId}\"}}",
        AutomationActionTypes.DmxSceneControl => $"{{\"TargetClientId\":\"{clientId}\",\"Operation\":\"StartScene\",\"SceneId\":\"{referenceId}\"}}",
        AutomationActionTypes.PlayAudioClip => $"{{\"TargetClientId\":\"{clientId}\",\"AudioClipId\":\"{referenceId}\"}}",
        _ => throw new ArgumentOutOfRangeException(nameof(actionType))
    };

    // Seeds the row an action type names, in the given company. Returns Guid.Empty for a type
    // that names nothing but its kiosk.
    private static async Task<Guid> SeedReferenceAsync(Harness harness, string actionType, int companyId, Guid clientId)
    {
        await using ApplicationDbContext ctx = new(harness.Options);
        Guid id = Guid.NewGuid();

        switch (actionType)
        {
            case AutomationActionTypes.MusicControl:
                ctx.Playlists.Add(new Playlist { Id = id, CompanyId = companyId, Name = "Party", CreateDate = DateTime.UtcNow });
                break;
            case AutomationActionTypes.StartProjectionProgram:
            case AutomationActionTypes.ProjectionProgramControl:
                ctx.ProjectionPrograms.Add(new ProjectionProgram { Id = id, CompanyId = companyId, Name = "Lobby" });
                break;
            case AutomationActionTypes.DmxSceneControl:
                ctx.DmxScenes.Add(new DmxScene { Id = id, CompanyId = companyId, ClientId = clientId, Name = "Scene", IsActive = true, CreateByUserId = "seed" });
                break;
            case AutomationActionTypes.PlayAudioClip:
                ctx.AudioClips.Add(new AudioClip { Id = id, CompanyId = companyId, Name = "Jingle" });
                break;
            default:
                return Guid.Empty;
        }

        await ctx.SaveChangesAsync();

        return id;
    }

    [TestMethod]
    [DataRow(AutomationActionTypes.MusicControl)]
    [DataRow(AutomationActionTypes.StartProjectionProgram)]
    [DataRow(AutomationActionTypes.StopProjectionProgram)]
    [DataRow(AutomationActionTypes.ProjectionProgramControl)]
    [DataRow(AutomationActionTypes.DmxSceneControl)]
    [DataRow(AutomationActionTypes.PlayAudioClip)]
    public async Task TestRun_NeverRunsAnActionAimedAtAnotherCompanysKiosk(string actionType)
    {
        Harness harness = await CreateHarnessAsync();
        Guid ownKiosk = await SeedKioskAsync(harness);
        Guid otherKiosk = await SeedKioskAsync(harness, OtherCompany);
        Guid ownReference = await SeedReferenceAsync(harness, actionType, OwnCompany, ownKiosk);
        Guid otherKioskReference = await SeedReferenceAsync(harness, actionType, OwnCompany, otherKiosk);

        AutomationRuleAction allowed = new() { Id = Guid.NewGuid(), ActionType = actionType, ParametersJson = ParametersFor(actionType, ownKiosk, ownReference), SortOrder = 0, IsActive = true };
        AutomationRuleAction foreign = new() { Id = Guid.NewGuid(), ActionType = actionType, ParametersJson = ParametersFor(actionType, otherKiosk, otherKioskReference), SortOrder = 1, IsActive = true };
        AutomationRule rule = await SeedRuleAsync(harness, ownKiosk, [allowed, foreign]);

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id));

        CollectionAssert.AreEqual(new[] { allowed.Id }, harness.AnyType.ExecutedActionIds, "Another company's kiosk must never reach the executor.");
        Assert.IsTrue(result.Data!.Actions[0].Success);
        Assert.IsFalse(result.Data.Actions[1].Success);
        Assert.AreEqual("The action's kiosk belongs to a different company", result.Data.Actions[1].ErrorMessage);
    }

    [TestMethod]
    [DataRow(AutomationActionTypes.MusicControl, "playlist")]
    [DataRow(AutomationActionTypes.StartProjectionProgram, "projection program")]
    [DataRow(AutomationActionTypes.ProjectionProgramControl, "projection program")]
    [DataRow(AutomationActionTypes.DmxSceneControl, "scene")]
    [DataRow(AutomationActionTypes.PlayAudioClip, "audio clip")]
    public async Task TestRun_NeverRunsAnActionThatPlaysAnotherCompanysContent(string actionType, string described)
    {
        Harness harness = await CreateHarnessAsync();
        Guid ownKiosk = await SeedKioskAsync(harness);
        Guid otherReference = await SeedReferenceAsync(harness, actionType, OtherCompany, ownKiosk);

        AutomationRuleAction foreign = new() { Id = Guid.NewGuid(), ActionType = actionType, ParametersJson = ParametersFor(actionType, ownKiosk, otherReference), SortOrder = 0, IsActive = true };
        AutomationRule rule = await SeedRuleAsync(harness, ownKiosk, [foreign]);

        Result<AutomationTestRunResult> result = await harness.Engine.TestRunRuleAsync(Request(rule.Id));

        Assert.IsEmpty(harness.AnyType.ExecutedActionIds);
        Assert.AreEqual($"The action's {described} belongs to a different company", result.Data!.Actions.Single().ErrorMessage);
    }

    [TestMethod]
    public async Task RealTrigger_AlsoRefusesAnActionAimedAtAnotherCompanysKiosk()
    {
        Harness harness = await CreateHarnessAsync();
        Guid ownKiosk = await SeedKioskAsync(harness);
        Guid otherKiosk = await SeedKioskAsync(harness, OtherCompany);

        AutomationRuleAction foreign = new()
        {
            Id = Guid.NewGuid(),
            ActionType = AutomationActionTypes.MusicControl,
            ParametersJson = $"{{\"TargetClientId\":\"{otherKiosk}\",\"Operation\":\"Pause\"}}",
            IsActive = true
        };
        await SeedRuleAsync(harness, ownKiosk, [foreign]);

        await harness.Engine.ProcessTransitionAsync(new GameStatusTransitionEvent(ownKiosk, GameStatus.NotStarted, GameStatus.InGame), CancellationToken.None);

        Assert.IsEmpty(harness.AnyType.ExecutedActionIds);
        AutomationRuleExecution execution = (await GetExecutionsAsync(harness)).Single();
        Assert.IsFalse(execution.IsTestRun);
        Assert.AreEqual("The action's kiosk belongs to a different company", execution.ActionExecutions.Single().ErrorMessage);
    }

    [TestMethod]
    public async Task TestRun_DoesNotRaiseOnRuleExecuted_OrTouchTheAlertsPage()
    {
        AutomationRuleAction failing = TestAction();
        Harness harness = await CreateHarnessAsync(behaviour: _ => (false, "Client not connected"));
        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [failing]);
        bool raised = false;
        harness.Engine.OnRuleExecuted += (_, _, _) => raised = true;

        await harness.Engine.TestRunRuleAsync(Request(rule.Id));

        Assert.IsFalse(raised, "The dashboard widget's last run must ignore tests.");
        Assert.IsEmpty(harness.Alerts.Raised, "A failed test isn't a failing rule.");
        Assert.IsEmpty(harness.Alerts.Resolved);
    }

    [TestMethod]
    public async Task TestRun_DoesNotStopAScheduledRuleFiringLater_EvenAfterARestart()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [TestAction()], AutomationTriggerType.Scheduled);

        // 08:00 venue time: a successful test, an hour before the 09:00 schedule.
        Assert.IsTrue((await harness.Engine.TestRunRuleAsync(Request(rule.Id))).Data!.AllPassed);

        // A restart: a new engine reseeds "already fired today" from the log.
        Harness restarted = await CreateHarnessAsync(options: harness.Options);
        await restarted.Engine.ProcessScheduledRulesAsync(new DateTime(2026, 7, 15, 9, 1, 0), CancellationToken.None);

        Assert.HasCount(1, restarted.Executor.ExecutedActionIds, "The test row must not count as today's run.");
    }

    [TestMethod]
    public async Task TestRun_DoesNotUseUpTheIdleStretch()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [TestAction()], AutomationTriggerType.ClientIdle);

        harness.Engine.EnqueueTransition(kioskId, GameStatus.NotStarted);
        await harness.Engine.TestRunRuleAsync(Request(rule.Id));
        harness.Executor.ExecutedActionIds.Clear();

        await harness.Engine.ProcessIdleRulesAsync(DateTime.UtcNow.AddMinutes(31), CancellationToken.None);

        Assert.HasCount(1, harness.Executor.ExecutedActionIds, "The idle rule still fires once the kiosk has been idle long enough.");
    }

    // An executor that waits until released, so a second test can arrive mid-run.
    private sealed class GateExecutor : IActionExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanHandle(string actionType) => actionType == RecordingActionExecutor.TestActionType;

        public async Task<(bool Success, string? ErrorMessage)> ExecuteAsync(AutomationRuleAction action, Guid triggerClientId)
        {
            Started.TrySetResult();
            await Release.Task;
            return (true, null);
        }
    }

    [TestMethod]
    public async Task TestRun_RefusesASecondTestOfTheSameRuleWhileTheFirstIsRunning()
    {
        Harness harness = await CreateHarnessAsync();
        Guid kioskId = await SeedKioskAsync(harness);
        AutomationRule rule = await SeedRuleAsync(harness, kioskId, [TestAction()]);
        GateExecutor gate = new();

        AutomationEngineService engine = new(
            harness.Factory,
            [gate],
            new LaserGameStatusStore(),
            new RecordingOperationsAlertWriter(),
            harness.Clock,
            NullLogger<AutomationEngineService>.Instance,
            new NoPartiesSchedule());

        Task<Result<AutomationTestRunResult>> first = engine.TestRunRuleAsync(Request(rule.Id));
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Result<AutomationTestRunResult> second = await engine.TestRunRuleAsync(Request(rule.Id));

        gate.Release.SetResult();
        Result<AutomationTestRunResult> firstResult = await first;

        Assert.AreEqual("A test run of this rule is already in progress.", second.Error);
        Assert.IsTrue(firstResult.IsSuccess, firstResult.Error);
        Result<AutomationTestRunResult> third = await engine.TestRunRuleAsync(Request(rule.Id));
        Assert.IsTrue(third.IsSuccess, "Once the first has finished, the rule can be tested again.");
    }
}
