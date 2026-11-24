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
/// Party-time trigger behaviour. ProcessPartyRulesAsync takes "now", and the party schedule is a
/// fake the test edits between ticks, so moves and cancellations can be driven tick by tick.
/// </summary>
[TestClass]
public class AutomationEngineServicePartyTests
{
    private const int CompanyId = 1;
    private const int LocationId = 10;
    private const string ChildName = "Ava Testchild";

    private static readonly DateTime Now = new(2026, 11, 21, 14, 0, 30, DateTimeKind.Utc);

    private sealed class FakePartySchedule : IPartyScheduleProvider
    {
        public List<PartySlot> Parties { get; } = [];
        public bool Fail { get; set; }
        public List<(int CompanyId, int LocationId)> Reads { get; } = [];

        public Task<Result<IReadOnlyList<PartySlot>>> GetDayAsync(int companyId, int locationId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            Reads.Add((companyId, locationId));

            if (Fail)
            {
                return Task.FromResult(Result<IReadOnlyList<PartySlot>>.Fail("database is down"));
            }

            // The real provider filters by company and location; the fake does the same so the
            // engine's grouping is what's under test.
            IReadOnlyList<PartySlot> slots = companyId == CompanyId ? [.. Parties.Where(p => p.LocationId == locationId)] : [];
            return Task.FromResult(Result<IReadOnlyList<PartySlot>>.Ok(slots));
        }

        public void Invalidate(int locationId)
        {
        }
    }

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static AutomationEngineService GetEngine(DbContextOptions<ApplicationDbContext> options, FakePartySchedule schedule, IActionExecutor executor)
    {
        Mock<ISystemDbContextFactory> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));

        return new AutomationEngineService(
            factory.Object, [executor], new LaserGameStatusStore(), new Lanyard.Tests.Services.OperationsAlerts.RecordingOperationsAlertWriter(),
            TimeProvider.System, NullLogger<AutomationEngineService>.Instance, schedule);
    }

    private static async Task SeedEngineEnabledAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = "AutomationEngine.Enabled", Value = "true" });
        await ctx.SaveChangesAsync();
    }

    private static async Task<Guid> SeedPartyRuleAsync(
        DbContextOptions<ApplicationDbContext> options,
        PartyMilestone milestone,
        int leadMinutes,
        string? room = null,
        int companyId = CompanyId,
        AutomationRuleCondition? condition = null,
        Guid? ruleId = null)
    {
        Guid id = ruleId ?? Guid.NewGuid();

        await using ApplicationDbContext ctx = new(options);
        ctx.AutomationRules.Add(new AutomationRule
        {
            Id = id,
            CompanyId = companyId,
            Name = "Party rule",
            TriggerClientId = Guid.NewGuid(),
            TriggerType = AutomationTriggerType.PartyMilestone,
            TriggerEvent = GameStatus.NotStarted,
            PartyLocationId = LocationId,
            PartyMilestone = milestone,
            PartyLeadMinutes = leadMinutes,
            PartyRoom = room,
            IsActive = true,
            IsEnabled = true,
            CreateDate = DateTime.UtcNow,
            Actions =
            [
                new AutomationRuleAction { Id = Guid.NewGuid(), ActionType = RecordingActionExecutor.TestActionType, ParametersJson = "{}", SortOrder = 0, IsActive = true }
            ],
            Conditions = condition is null ? [] : [condition]
        });
        await ctx.SaveChangesAsync();

        return id;
    }

    private static PartySlot Party(DateTime startUtc, DateTime? laserUtc = null, DateTime? eatUtc = null, string? room = null, DateTime? createDate = null) =>
        new(Guid.NewGuid(), LocationId, startUtc, startUtc.AddHours(2), eatUtc, laserUtc, room, ChildName, 7, createDate ?? new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

    private static async Task<List<AutomationRuleExecution>> ExecutionsAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);
        return await ctx.AutomationRuleExecutions.OrderBy(e => e.ExecutedAt).ToListAsync();
    }

    private static DateTime At(int hour, int minute) => new(2026, 11, 21, hour, minute, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow(PartyMilestone.Start)]
    [DataRow(PartyMilestone.EatTime)]
    [DataRow(PartyMilestone.LaserTag)]
    [DataRow(PartyMilestone.End)]
    public async Task EachMilestone_FiresAtItsTime(PartyMilestone milestone)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, milestone, leadMinutes: 0);

        FakePartySchedule schedule = new();
        DateTime moment = At(14, 0);
        PartySlot party = milestone switch
        {
            PartyMilestone.Start => Party(moment),
            PartyMilestone.EatTime => Party(At(13, 0), eatUtc: moment),
            PartyMilestone.LaserTag => Party(At(13, 0), laserUtc: moment),
            _ => Party(At(12, 0))
        };
        schedule.Parties.Add(party);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, schedule, executor);

        await engine.ProcessPartyRulesAsync(Now, CancellationToken.None);

        Assert.HasCount(1, executor.ExecutedActionIds);
        List<AutomationRuleExecution> runs = await ExecutionsAsync(options);
        Assert.HasCount(1, runs);
        Assert.AreEqual(party.Id, runs[0].PartyBookingId);
    }

    [TestMethod]
    public async Task PartyWithoutTheMilestone_DoesNotFire()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.LaserTag, 0);

        FakePartySchedule schedule = new();
        schedule.Parties.Add(Party(At(14, 0)));

        RecordingActionExecutor executor = new();
        await GetEngine(options, schedule, executor).ProcessPartyRulesAsync(Now, CancellationToken.None);

        Assert.IsEmpty(executor.ExecutedActionIds);
    }

    [TestMethod]
    [DataRow(0, 14, 0, true)]      // at the time
    [DataRow(120, 16, 0, true)]    // the longest lead
    [DataRow(10, 14, 10, true)]    // 10 min before 14:10
    [DataRow(10, 14, 20, false)]   // not yet due
    [DataRow(0, 13, 54, false)]    // 6m30s late: past the 5-minute catch-up window
    public async Task LeadMinutes_DecideWhenItFires(int lead, int hour, int minute, bool expected)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.Start, lead);

        FakePartySchedule schedule = new();
        schedule.Parties.Add(Party(At(hour, minute)));

        RecordingActionExecutor executor = new();
        await GetEngine(options, schedule, executor).ProcessPartyRulesAsync(Now, CancellationToken.None);

        Assert.AreEqual(expected ? 1 : 0, executor.ExecutedActionIds.Count);
    }

    [TestMethod]
    public void IsDue_CatchUpWindowEdges()
    {
        TimeSpan window = TimeSpan.FromMinutes(5);
        DateTime milestone = At(14, 0);

        Assert.IsFalse(PartyTriggerRules.IsDue(milestone, 0, milestone.AddSeconds(-1), window));
        Assert.IsTrue(PartyTriggerRules.IsDue(milestone, 0, milestone, window));
        Assert.IsTrue(PartyTriggerRules.IsDue(milestone, 0, milestone.AddMinutes(5), window));
        Assert.IsFalse(PartyTriggerRules.IsDue(milestone, 0, milestone.AddMinutes(5).AddSeconds(1), window));
        Assert.IsTrue(PartyTriggerRules.IsDue(milestone, 120, milestone.AddMinutes(-120), window));
    }

    [TestMethod]
    public async Task FiresOncePerParty_AcrossTicks()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.Start, 0);

        FakePartySchedule schedule = new();
        schedule.Parties.Add(Party(At(14, 0)));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, schedule, executor);

        await engine.ProcessPartyRulesAsync(Now, CancellationToken.None);
        await engine.ProcessPartyRulesAsync(Now.AddMinutes(1), CancellationToken.None);
        await engine.ProcessPartyRulesAsync(Now.AddMinutes(2), CancellationToken.None);

        Assert.HasCount(1, executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task PartiesAtTheSameMoment_FireOnce_LoggedAgainstTheFirst()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.Start, 0);

        FakePartySchedule schedule = new();
        PartySlot earlierBooked = Party(At(14, 0), createDate: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        PartySlot laterBooked = Party(At(14, 0), createDate: new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
        schedule.Parties.Add(laterBooked);
        schedule.Parties.Add(earlierBooked);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, schedule, executor);

        await engine.ProcessPartyRulesAsync(Now, CancellationToken.None);
        await engine.ProcessPartyRulesAsync(Now.AddMinutes(1), CancellationToken.None);

        Assert.HasCount(1, executor.ExecutedActionIds, "Two 14:00 parties must not start the scene twice.");
        List<AutomationRuleExecution> runs = await ExecutionsAsync(options);
        Assert.HasCount(1, runs);
        Assert.AreEqual(earlierBooked.Id, runs[0].PartyBookingId);
        Assert.AreEqual("Party: party start +1 other party", runs[0].TriggerEvent);
    }

    [TestMethod]
    public async Task ExecutionLog_HoldsThePartyId_NeverTheChildsName()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.LaserTag, 10, room: "Room 2");

        FakePartySchedule schedule = new();
        PartySlot party = Party(At(13, 0), laserUtc: At(14, 10), room: "Room 2");
        schedule.Parties.Add(party);

        await GetEngine(options, schedule, new RecordingActionExecutor()).ProcessPartyRulesAsync(Now, CancellationToken.None);

        AutomationRuleExecution run = (await ExecutionsAsync(options)).Single();
        Assert.AreEqual(party.Id, run.PartyBookingId);
        Assert.AreEqual("Party: laser tag (10 min before)", run.TriggerEvent);

        foreach (string? text in new[] { run.TriggerEvent, run.RuleName, run.SkipReason })
        {
            Assert.IsFalse((text ?? string.Empty).Contains("Ava", StringComparison.OrdinalIgnoreCase), $"\"{text}\" must not contain the child's name.");
        }
    }

    [TestMethod]
    public async Task CancelledBeforeItsMoment_NeverFires()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.Start, 0);

        FakePartySchedule schedule = new();
        PartySlot party = Party(At(14, 0));
        schedule.Parties.Add(party);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, schedule, executor);

        await engine.ProcessPartyRulesAsync(At(13, 50), CancellationToken.None);
        schedule.Parties.Remove(party); // cancelled: the provider stops returning it
        await engine.ProcessPartyRulesAsync(Now, CancellationToken.None);

        Assert.IsEmpty(executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task MovedLaterBeforeFiring_FiresAtTheNewTime()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.Start, 0);

        FakePartySchedule schedule = new();
        PartySlot party = Party(At(14, 0));
        schedule.Parties.Add(party);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, schedule, executor);

        await engine.ProcessPartyRulesAsync(At(13, 50), CancellationToken.None);
        schedule.Parties[0] = party with { StartUtc = At(15, 0), EndUtc = At(17, 0) };

        await engine.ProcessPartyRulesAsync(Now, CancellationToken.None);
        Assert.IsEmpty(executor.ExecutedActionIds, "14:00 is no longer the party's start.");

        await engine.ProcessPartyRulesAsync(At(15, 0).AddSeconds(30), CancellationToken.None);
        Assert.HasCount(1, executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task MovedLaterAfterFiring_DoesNotFireAgain()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.Start, 0);

        FakePartySchedule schedule = new();
        PartySlot party = Party(At(14, 0));
        schedule.Parties.Add(party);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, schedule, executor);

        await engine.ProcessPartyRulesAsync(Now, CancellationToken.None);
        schedule.Parties[0] = party with { StartUtc = At(14, 30), EndUtc = At(16, 30) };
        await engine.ProcessPartyRulesAsync(At(14, 30).AddSeconds(30), CancellationToken.None);

        Assert.HasCount(1, executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task AfterARestart_TheLogStopsItFiringAgain_IncludingMergedParties()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        Guid ruleId = await SeedPartyRuleAsync(options, PartyMilestone.Start, 0);

        FakePartySchedule schedule = new();
        schedule.Parties.Add(Party(At(14, 0), createDate: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
        schedule.Parties.Add(Party(At(14, 0), createDate: new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc)));

        RecordingActionExecutor before = new();
        await GetEngine(options, schedule, before).ProcessPartyRulesAsync(Now, CancellationToken.None);
        Assert.HasCount(1, before.ExecutedActionIds);

        // The log row's ExecutedAt is the real clock; pin it to the tick so the reseed lines up.
        await using (ApplicationDbContext ctx = new(options))
        {
            AutomationRuleExecution run = await ctx.AutomationRuleExecutions.SingleAsync(e => e.AutomationRuleId == ruleId);
            run.ExecutedAt = Now;
            await ctx.SaveChangesAsync();
        }

        RecordingActionExecutor after = new();
        await GetEngine(options, schedule, after).ProcessPartyRulesAsync(Now.AddMinutes(1), CancellationToken.None);

        Assert.IsEmpty(after.ExecutedActionIds, "Neither the logged party nor the merged one should fire again.");
    }

    [TestMethod]
    public async Task ConditionNotMet_SkipLoggedOnce_AndRetriedInTheWindow()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);

        // A day-of-week condition naming every day but today (venue time) never passes.
        DayOfWeek today = RotaTime.ToVenueLocal(DateTime.UtcNow).DayOfWeek;
        string otherDays = string.Join(",", Enum.GetValues<DayOfWeek>().Where(d => d != today).Select(d => (int)d));
        Guid conditionId = Guid.NewGuid();
        await SeedPartyRuleAsync(options, PartyMilestone.Start, 0, condition: new AutomationRuleCondition
        {
            Id = conditionId,
            ConditionType = AutomationConditionType.DaysOfWeek,
            ParametersJson = AutomationConditionParameters.Serialize(new DaysOfWeekConditionParameters(otherDays)),
            IsActive = true
        });

        FakePartySchedule schedule = new();
        PartySlot party = Party(At(14, 0));
        schedule.Parties.Add(party);

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, schedule, executor);

        await engine.ProcessPartyRulesAsync(Now, CancellationToken.None);
        await engine.ProcessPartyRulesAsync(Now.AddMinutes(1), CancellationToken.None);

        List<AutomationRuleExecution> runs = await ExecutionsAsync(options);
        Assert.HasCount(1, runs, "The skip is logged once, not every tick.");
        Assert.IsNotNull(runs[0].SkipReason);
        Assert.AreEqual(party.Id, runs[0].PartyBookingId);
        Assert.IsEmpty(executor.ExecutedActionIds);

        // The condition goes away inside the window: the next tick still fires it.
        await using (ApplicationDbContext ctx = new(options))
        {
            AutomationRuleCondition condition = await ctx.AutomationRuleConditions.SingleAsync(c => c.Id == conditionId);
            condition.IsActive = false;
            await ctx.SaveChangesAsync();
        }

        engine.InvalidateRuleCache();
        await engine.ProcessPartyRulesAsync(Now.AddMinutes(2), CancellationToken.None);

        Assert.HasCount(1, executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task RoomFilter_OnlyMatchingRoomsFire_IgnoringCaseAndSpaces()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.Start, 0, room: "Room 2");

        FakePartySchedule schedule = new();
        PartySlot other = Party(At(14, 0), room: "Room 1");
        PartySlot matching = Party(At(14, 0), room: "  room   2 ");
        schedule.Parties.Add(other);
        schedule.Parties.Add(matching);

        RecordingActionExecutor executor = new();
        await GetEngine(options, schedule, executor).ProcessPartyRulesAsync(Now, CancellationToken.None);

        AutomationRuleExecution run = (await ExecutionsAsync(options)).Single();
        Assert.AreEqual(matching.Id, run.PartyBookingId);
        Assert.AreEqual("Party: party start", run.TriggerEvent, "Room 1's party isn't merged in.");
    }

    [TestMethod]
    public async Task AnotherCompanysParties_NeverFireARule()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.Start, 0, companyId: 2);

        FakePartySchedule schedule = new();
        schedule.Parties.Add(Party(At(14, 0)));

        RecordingActionExecutor executor = new();
        await GetEngine(options, schedule, executor).ProcessPartyRulesAsync(Now, CancellationToken.None);

        Assert.IsEmpty(executor.ExecutedActionIds);
        CollectionAssert.Contains(schedule.Reads, (2, LocationId), "Parties are read for the rule's own company.");
    }

    [TestMethod]
    public async Task ScheduleUnavailable_NothingFires_AndTheNextTickRecovers()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.Start, 0);

        FakePartySchedule schedule = new() { Fail = true };
        schedule.Parties.Add(Party(At(14, 0)));

        RecordingActionExecutor executor = new();
        AutomationEngineService engine = GetEngine(options, schedule, executor);

        await engine.ProcessPartyRulesAsync(Now, CancellationToken.None);
        Assert.IsEmpty(executor.ExecutedActionIds);

        schedule.Fail = false;
        await engine.ProcessPartyRulesAsync(Now.AddMinutes(1), CancellationToken.None);
        Assert.HasCount(1, executor.ExecutedActionIds);
    }

    [TestMethod]
    public async Task TwoRulesAtOneLocation_ReadThePartiesOnce()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedEngineEnabledAsync(options);
        await SeedPartyRuleAsync(options, PartyMilestone.Start, 0);
        await SeedPartyRuleAsync(options, PartyMilestone.End, 0);

        FakePartySchedule schedule = new();
        schedule.Parties.Add(Party(At(14, 0)));

        await GetEngine(options, schedule, new RecordingActionExecutor()).ProcessPartyRulesAsync(Now, CancellationToken.None);

        Assert.HasCount(1, schedule.Reads);
    }

    [TestMethod]
    public void ExecutionLabel_Wording()
    {
        Assert.AreEqual("Party: party start", PartyTriggerRules.ExecutionLabel(PartyMilestone.Start, 0, 0));
        Assert.AreEqual("Party: eat time (15 min before)", PartyTriggerRules.ExecutionLabel(PartyMilestone.EatTime, 15, 0));
        Assert.AreEqual("Party: party end +2 other parties", PartyTriggerRules.ExecutionLabel(PartyMilestone.End, 0, 2));
        Assert.AreEqual("10 min before laser tag", PartyTriggerRules.Describe(PartyMilestone.LaserTag, 10));
        Assert.AreEqual("At party start", PartyTriggerRules.Describe(PartyMilestone.Start, 0));
    }

    [TestMethod]
    [DataRow(null, PartyMilestone.Start, 0, null, "needs a location")]
    [DataRow(10, null, 0, null, "which part of the party")]
    [DataRow(10, PartyMilestone.Start, -1, null, "between 0 and 120")]
    [DataRow(10, PartyMilestone.Start, 121, null, "between 0 and 120")]
    [DataRow(10, PartyMilestone.Start, null, null, "between 0 and 120")]
    public void Validate_RejectsBadSettings(int? location, PartyMilestone? milestone, int? lead, string? room, string expected)
    {
        StringAssert.Contains(PartyTriggerRules.Validate(location, milestone, lead, room), expected);
    }

    [TestMethod]
    public void Validate_AcceptsGoodSettings_AndRejectsALongRoom()
    {
        Assert.IsNull(PartyTriggerRules.Validate(10, PartyMilestone.LaserTag, 120, "Room 2"));
        Assert.IsNull(PartyTriggerRules.Validate(10, PartyMilestone.Start, 0, null));
        StringAssert.Contains(PartyTriggerRules.Validate(10, PartyMilestone.Start, 0, new string('x', 101)), "at most 100");
    }

    [TestMethod]
    public void PartyRooms_MatchTrimmedAndIgnoringCase()
    {
        Assert.IsTrue(PartyRooms.Matches(null, "Room 2"));
        Assert.IsTrue(PartyRooms.Matches("  ", null));
        Assert.IsTrue(PartyRooms.Matches("Room 2", " room  2"));
        Assert.IsFalse(PartyRooms.Matches("Room 2", "Room 21"));
        Assert.IsFalse(PartyRooms.Matches("Room 2", null));
        Assert.IsNull(PartyRooms.Normalise("   "));
    }
}
