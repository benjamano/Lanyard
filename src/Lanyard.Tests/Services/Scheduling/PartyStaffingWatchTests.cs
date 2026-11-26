using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Application.Services.Parties;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.OperationsAlerts;
using Lanyard.Tests.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

// The watch end to end on EF InMemory with the real alert writer: party saves, cancels, staffing
// rule changes and Auto-build raise and clear "Party staffing out of date" alerts.
[TestClass]
public class PartyStaffingWatchTests
{
    // Saturday 3 October 2026, 09:00 UTC. The next two Saturdays (10th, 17th) are inside the 14-day
    // horizon; the one after isn't.
    private static readonly DateTime Now = new(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Saturday = new(2026, 10, 10);
    private const string ActingUser = "acting-manager";

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private sealed record Harness(
        DbContextOptions<ApplicationDbContext> Options,
        Company Company,
        Location Ipswich,
        StaffPosition Host,
        UserProfile Amy,
        UserProfile Manager,
        RecordingNotificationDispatcher Notifications,
        PartyStaffingWatch Watch,
        PartyBookingService Bookings,
        PartyStaffingService Staffing,
        RotaAutoBuildService AutoBuild)
    {
        public LocationScope Scope => SchedulingTestHelpers.AdminScope;

        public List<NotificationJob> StaffingNotices =>
            [.. Notifications.Jobs.Where(x => x.Topic == NotificationTopic.PartyStaffingOutOfDate)];

        public async Task<List<OperationsAlert>> OpenAlertsAsync()
        {
            await using ApplicationDbContext ctx = new(Options);
            return await ctx.OperationsAlerts.IgnoreQueryFilters()
                .Where(x => x.Type == OperationsAlertType.PartyStaffingOutOfDate && x.Status != OperationsAlertStatus.Resolved)
                .ToListAsync();
        }
    }

    // Ipswich: a Saturday 09:00-14:00 template needing one Host, a default party staffing rule of
    // one Host, Amy and Tom (Hosts) and a Manager. Amy works the template shift in BuildDayAsync, so
    // Auto-build gives party shifts to Tom.
    private static async Task<Harness> SeedAsync(DateTime? now = null)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition host = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Host");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Tom");
        UserProfile manager = await OperationsAlertTestSupport.SeedPersonAsync(options, ipswich, "Mia", "Manager");

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = amy.Id, StaffPositionId = host.Id, IsPrimary = true, CreateDate = Now });
            ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = tom.Id, StaffPositionId = host.Id, IsPrimary = true, CreateDate = Now });

            ShiftTemplate template = new()
            {
                Id = Guid.NewGuid(),
                CompanyId = company.Id,
                LocationId = ipswich.Id,
                Name = "Saturday AM",
                DayOfWeek = DayOfWeek.Saturday,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(14, 0)
            };
            template.Slots.Add(new ShiftTemplateSlot { Id = Guid.NewGuid(), ShiftTemplateId = template.Id, StaffPositionId = host.Id, Headcount = 1 });
            ctx.ShiftTemplates.Add(template);

            ctx.PartyStaffingRules.Add(new PartyStaffingRule
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, LocationId = ipswich.Id, StaffPositionId = host.Id, StaffCount = 1
            });

            await ctx.SaveChangesAsync();
        }

        ISystemDbContextFactory factory = SchedulingTestHelpers.GetFactory(options);
        FixedClock clock = new(now ?? Now);
        RecordingNotificationDispatcher notifications = new();
        OperationsAlertWriter writer = new(factory, notifications, new OperationsAlertEventBus(), clock, NullLogger<OperationsAlertWriter>.Instance);
        PartyStaffingWatch watch = new(factory, writer, notifications, clock, NullLogger<PartyStaffingWatch>.Instance);

        return new Harness(options, company, ipswich, host, amy, manager, notifications, watch,
            new PartyBookingService(factory, null, watch),
            new PartyStaffingService(factory, NullLogger<PartyStaffingService>.Instance, watch),
            new RotaAutoBuildService(factory, new ContractRequirementService(factory), NullLogger<RotaAutoBuildService>.Instance, clock, watch));
    }

    private static PartyBooking NewParty(Harness h, int startHour = 10, int endHour = 12, DateOnly? day = null) => new()
    {
        CompanyId = h.Company.Id,
        LocationId = h.Ipswich.Id,
        StartUtc = RotaTime.ToUtc(day ?? Saturday, new TimeOnly(startHour, 0)),
        EndUtc = RotaTime.ToUtc(day ?? Saturday, new TimeOnly(endHour, 0)),
        PartyType = "Play",
        ChildName = "Lily",
        ChildAgeTurning = 7,
        ContactName = "Sam Parent",
        ContactPhone = "07700 900123",
        ExpectedChildren = 15,
        MenuType = PartyMenuType.Hot
    };

    private static async Task<PartyBooking> BookAsync(Harness h, PartyBooking party)
    {
        Result<PartyBooking> saved = await h.Bookings.SaveBookingAsync(h.Scope, party, ActingUser);
        Assert.IsTrue(saved.IsSuccess, saved.Error);
        return saved.Data!;
    }

    // The day's rota, built: the template's Host shift for Amy, published or not.
    private static async Task<Shift> BuildDayAsync(Harness h, DateOnly? day = null, bool published = false, Guid? partyId = null)
    {
        await using ApplicationDbContext ctx = new(h.Options);

        Shift shift = new()
        {
            Id = Guid.NewGuid(),
            LocationId = h.Ipswich.Id,
            UserId = h.Amy.Id,
            StaffPositionId = h.Host.Id,
            StartUtc = RotaTime.ToUtc(day ?? Saturday, new TimeOnly(9, 0)),
            EndUtc = RotaTime.ToUtc(day ?? Saturday, new TimeOnly(14, 0)),
            PartyBookingId = partyId,
            PublishedDateUtc = published ? Now : null,
            CreateDate = Now,
            CreateByUserId = "seed",
            IsActive = true
        };

        ctx.Shifts.Add(shift);
        await ctx.SaveChangesAsync();

        return shift;
    }

    [TestMethod]
    public async Task ManualSave_OnABuiltDay_RaisesAnAlertAndOneNotice()
    {
        Harness h = await SeedAsync();
        await BuildDayAsync(h);

        PartyBooking party = await BookAsync(h, NewParty(h));

        OperationsAlert alert = (await h.OpenAlertsAsync()).Single();
        Assert.AreEqual(PartyStaffingAlerts.DedupKey(h.Ipswich.Id, Saturday), alert.DedupKey);
        Assert.AreEqual(h.Ipswich.Id, alert.LocationId);
        Assert.AreEqual(h.Company.Id, alert.CompanyId);
        Assert.AreEqual(PartyStaffingAlerts.RotaLink(h.Ipswich.Id, Saturday), alert.LinkUrl);
        StringAssert.Contains(alert.Title, "Sat 10 Oct");
        StringAssert.Contains(alert.Detail, "Party: Play needs 1 more Host");
        Assert.IsFalse(alert.Detail!.Contains("Lily"), "never the child's name");

        NotificationJob notice = h.StaffingNotices.Single();
        Assert.AreEqual(h.Manager.Id, notice.UserId);

        Result<PartyStaffingAlertView?> banner = await h.Bookings.GetStaffingAlertAsync(h.Scope, party.Id);
        Assert.IsTrue(banner.IsSuccess, banner.Error);
        Assert.AreEqual(Saturday, banner.Data!.Day);
    }

    [TestMethod]
    public async Task ManualSave_DayWithNoRotaYet_NothingRaised()
    {
        Harness h = await SeedAsync();

        PartyBooking party = await BookAsync(h, NewParty(h));

        Assert.AreEqual(0, (await h.OpenAlertsAsync()).Count);
        Assert.AreEqual(0, h.StaffingNotices.Count);
        Assert.IsNull((await h.Bookings.GetStaffingAlertAsync(h.Scope, party.Id)).Data);
    }

    [TestMethod]
    public async Task ManualSave_BeyondTheHorizon_NothingRaised()
    {
        Harness h = await SeedAsync();
        DateOnly farSaturday = Saturday.AddDays(14); // the 24th, 21 days out
        await BuildDayAsync(h, farSaturday);

        await BookAsync(h, NewParty(h, day: farSaturday));

        Assert.AreEqual(0, (await h.OpenAlertsAsync()).Count);
    }

    [TestMethod]
    public async Task ManualSave_ActingUserIsLeftOutOfTheNotice()
    {
        Harness h = await SeedAsync();
        await BuildDayAsync(h);

        Result<PartyBooking> saved = await h.Bookings.SaveBookingAsync(h.Scope, NewParty(h), h.Manager.Id);
        Assert.IsTrue(saved.IsSuccess, saved.Error);

        Assert.AreEqual(1, (await h.OpenAlertsAsync()).Count, "the alert is still raised");
        Assert.AreEqual(0, h.StaffingNotices.Count, "but the manager who made the change isn't pushed about it");
    }

    [TestMethod]
    public async Task SecondChangeSameDay_RefreshesWithoutANewNotice()
    {
        Harness h = await SeedAsync();
        await BuildDayAsync(h);

        await BookAsync(h, NewParty(h, 10, 12));
        await BookAsync(h, NewParty(h, 11, 13));

        Assert.AreEqual(1, (await h.OpenAlertsAsync()).Count);
        Assert.AreEqual(1, h.StaffingNotices.Count);
    }

    [TestMethod]
    public async Task AutoBuildForTheDay_ClearsTheAlert()
    {
        Harness h = await SeedAsync();
        await BuildDayAsync(h);
        await BookAsync(h, NewParty(h));

        Result<AutoBuildResult> built = await h.AutoBuild.ApplyAsync(h.Scope, h.Ipswich.Id, Saturday, Saturday, ActingUser);

        Assert.IsTrue(built.IsSuccess, built.Error);
        Assert.AreEqual(0, (await h.OpenAlertsAsync()).Count);
    }

    [TestMethod]
    public async Task MovedToAnotherDay_ChecksBothDays()
    {
        Harness h = await SeedAsync();
        DateOnly nextSaturday = Saturday.AddDays(7);
        await BuildDayAsync(h);
        await BuildDayAsync(h, nextSaturday);
        PartyBooking party = await BookAsync(h, NewParty(h));
        await h.AutoBuild.ApplyAsync(h.Scope, h.Ipswich.Id, Saturday, Saturday, ActingUser);

        party.StartUtc = RotaTime.ToUtc(nextSaturday, new TimeOnly(10, 0));
        party.EndUtc = RotaTime.ToUtc(nextSaturday, new TimeOnly(12, 0));
        await BookAsync(h, party);

        List<string> keys = [.. (await h.OpenAlertsAsync()).Select(x => x.DedupKey).Order()];
        CollectionAssert.AreEquivalent(new[]
        {
            PartyStaffingAlerts.DedupKey(h.Ipswich.Id, Saturday),      // its party shift is now stale
            PartyStaffingAlerts.DedupKey(h.Ipswich.Id, nextSaturday)   // and its staff are missing here
        }, keys);
    }

    [TestMethod]
    public async Task CancelWithDraftShift_RaisesThenAutoBuildClears()
    {
        Harness h = await SeedAsync();
        await BuildDayAsync(h);
        PartyBooking party = await BookAsync(h, NewParty(h));
        await h.AutoBuild.ApplyAsync(h.Scope, h.Ipswich.Id, Saturday, Saturday, ActingUser);
        Assert.AreEqual(0, (await h.OpenAlertsAsync()).Count);

        Result<PartyBooking> cancelled = await h.Bookings.SetCancelledAsync(h.Scope, party.Id, true, ActingUser);
        Assert.IsTrue(cancelled.IsSuccess, cancelled.Error);

        OperationsAlert alert = (await h.OpenAlertsAsync()).Single();
        StringAssert.Contains(alert.Detail, "draft");

        await h.AutoBuild.ApplyAsync(h.Scope, h.Ipswich.Id, Saturday, Saturday, ActingUser);

        Assert.AreEqual(0, (await h.OpenAlertsAsync()).Count, "Auto-build took the draft off");
    }

    [TestMethod]
    public async Task CancelWithPublishedShift_StaysOpenUntilTheShiftIsRemoved()
    {
        Harness h = await SeedAsync();
        await BuildDayAsync(h);
        PartyBooking party = await BookAsync(h, NewParty(h));
        await h.AutoBuild.ApplyAsync(h.Scope, h.Ipswich.Id, Saturday, Saturday, ActingUser);

        // Publish the party's shift, then cancel the party.
        await using (ApplicationDbContext ctx = new(h.Options))
        {
            Shift partyShift = await ctx.Shifts.SingleAsync(x => x.PartyBookingId == party.Id);
            partyShift.MarkPublished(Now, ActingUser);
            await ctx.SaveChangesAsync();
        }

        await h.Bookings.SetCancelledAsync(h.Scope, party.Id, true, ActingUser);

        OperationsAlert alert = (await h.OpenAlertsAsync()).Single();
        StringAssert.Contains(alert.Detail, "published");
        StringAssert.Contains(alert.Detail, "Tom");

        // Auto-build never removes a published shift, so the alert stays.
        await h.AutoBuild.ApplyAsync(h.Scope, h.Ipswich.Id, Saturday, Saturday, ActingUser);
        Assert.AreEqual(1, (await h.OpenAlertsAsync()).Count);

        // The manager removes it on the rota; the recheck sweep clears the alert.
        await using (ApplicationDbContext ctx = new(h.Options))
        {
            Shift partyShift = await ctx.Shifts.SingleAsync(x => x.PartyBookingId == party.Id);
            partyShift.IsActive = false;
            partyShift.RemovalPending = true;
            await ctx.SaveChangesAsync();
        }

        Result<int> cleared = await h.Watch.RecheckOpenAsync();

        Assert.AreEqual(1, cleared.Data);
        Assert.AreEqual(0, (await h.OpenAlertsAsync()).Count);
    }

    [TestMethod]
    public async Task Recheck_NeverRaisesOrReopensAManuallyResolvedAlert()
    {
        Harness h = await SeedAsync();
        await BuildDayAsync(h);
        await BookAsync(h, NewParty(h));

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            OperationsAlert alert = await ctx.OperationsAlerts.IgnoreQueryFilters().SingleAsync();
            alert.Status = OperationsAlertStatus.Resolved;
            alert.Resolution = OperationsAlertResolution.Manual;
            await ctx.SaveChangesAsync();
        }

        Result<int> cleared = await h.Watch.RecheckOpenAsync();

        Assert.AreEqual(0, cleared.Data);
        Assert.AreEqual(0, (await h.OpenAlertsAsync()).Count);
    }

    [TestMethod]
    public async Task Recheck_PastDay_Cleared()
    {
        Harness h = await SeedAsync();
        await BuildDayAsync(h);
        await BookAsync(h, NewParty(h));

        // A watch whose clock is the Monday after: the Saturday is over.
        ISystemDbContextFactory factory = SchedulingTestHelpers.GetFactory(h.Options);
        FixedClock later = new(new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc));
        OperationsAlertWriter writer = new(factory, h.Notifications, new OperationsAlertEventBus(), later, NullLogger<OperationsAlertWriter>.Instance);
        PartyStaffingWatch watch = new(factory, writer, h.Notifications, later, NullLogger<PartyStaffingWatch>.Instance);

        Result<int> cleared = await watch.RecheckOpenAsync();

        Assert.AreEqual(1, cleared.Data);
        Assert.AreEqual(0, (await h.OpenAlertsAsync()).Count);
    }

    [TestMethod]
    public async Task PartiesOrRotaSwitchedOff_NothingRaisedAndOpenAlertsCleared()
    {
        foreach (CompanyFeature feature in new[] { CompanyFeature.Parties, CompanyFeature.StaffScheduling })
        {
            Harness h = await SeedAsync();
            await BuildDayAsync(h);
            PartyBooking party = await BookAsync(h, NewParty(h));
            Assert.AreEqual(1, (await h.OpenAlertsAsync()).Count);

            await using (ApplicationDbContext ctx = new(h.Options))
            {
                await TestFeatures.SwitchOffForCompanyAsync(ctx, h.Company.Id, feature);
                await ctx.SaveChangesAsync();
            }

            Result<PartyStaffingCheckOutcome> outcome = await h.Watch.CheckDaysAsync(h.Ipswich.Id, [Saturday]);

            Assert.IsTrue(outcome.IsSuccess, outcome.Error);
            Assert.AreEqual(0, (await h.OpenAlertsAsync()).Count, $"{feature} off clears it");
        }
    }

    [TestMethod]
    public async Task StaffingRuleChange_ChecksEveryWatchedDay()
    {
        Harness h = await SeedAsync();
        await BuildDayAsync(h);
        await BuildDayAsync(h, Saturday.AddDays(7));
        await BookAsync(h, NewParty(h));
        await BookAsync(h, NewParty(h, day: Saturday.AddDays(7)));
        await h.AutoBuild.ApplyAsync(h.Scope, h.Ipswich.Id, Saturday, Saturday.AddDays(7), ActingUser);
        h.Notifications.Jobs.Clear();
        Assert.AreEqual(0, (await h.OpenAlertsAsync()).Count);

        // Parties now need two Hosts each.
        PartyStaffingRule rule;
        await using (ApplicationDbContext ctx = new(h.Options))
        {
            rule = await ctx.PartyStaffingRules.AsNoTracking().SingleAsync();
        }

        rule.StaffCount = 2;
        Result<PartyStaffingRule> saved = await h.Staffing.SaveRuleAsync(h.Scope, rule, ActingUser);
        Assert.IsTrue(saved.IsSuccess, saved.Error);

        Assert.AreEqual(2, (await h.OpenAlertsAsync()).Count);
        NotificationJob notice = h.StaffingNotices.Single();
        StringAssert.Contains(((OperationsAlertPayload)notice.Payload).Title, "2 days", "one summary, not one per day");
    }

    [TestMethod]
    public async Task OtherCompaniesAlertsAndShiftsAreUntouched()
    {
        Harness h = await SeedAsync();
        await BuildDayAsync(h);

        (Company other, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(h.Options, "Other Co", "Elsewhere");
        await OperationsAlertTestSupport.SeedAlertAsync(h.Options, other.Id, elsewhere.Id, PartyStaffingAlerts.DedupKey(elsewhere.Id, Saturday));

        await BookAsync(h, NewParty(h));

        await using ApplicationDbContext ctx = new(h.Options);
        List<OperationsAlert> open = await ctx.OperationsAlerts.IgnoreQueryFilters().Where(x => x.Status != OperationsAlertStatus.Resolved).ToListAsync();
        Assert.AreEqual(1, open.Count(x => x.CompanyId == h.Company.Id && x.LocationId == h.Ipswich.Id));
        Assert.AreEqual(1, open.Count(x => x.CompanyId == other.Id), "another company's alert is left alone");
        Assert.IsTrue(h.StaffingNotices.All(x => x.UserId == h.Manager.Id));
    }

    [TestMethod]
    public async Task PaymentsAndNoteEdits_DontBringBackAnAlertResolvedByHand()
    {
        Harness h = await SeedAsync();
        await BuildDayAsync(h);
        PartyBooking party = await BookAsync(h, NewParty(h));
        int notices = h.StaffingNotices.Count;

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            OperationsAlert alert = await ctx.OperationsAlerts.IgnoreQueryFilters().SingleAsync();
            alert.Status = OperationsAlertStatus.Resolved;
            alert.Resolution = OperationsAlertResolution.Manual;
            await ctx.SaveChangesAsync();
        }

        Assert.IsTrue((await h.Bookings.SetDepositPaidAsync(h.Scope, party.Id, true, ActingUser)).IsSuccess);
        party.Notes = "Bringing their own cake";
        await BookAsync(h, party);

        Assert.AreEqual(0, (await h.OpenAlertsAsync()).Count, "nothing staffing depends on changed");
        Assert.AreEqual(notices, h.StaffingNotices.Count);
    }

    [TestMethod]
    public async Task MovedWithAPublishedShift_NewDayAloneGivesTheSameAnswer()
    {
        Harness h = await SeedAsync();
        DateOnly nextSaturday = Saturday.AddDays(7);
        await BuildDayAsync(h);
        await BuildDayAsync(h, nextSaturday);
        PartyBooking party = await BookAsync(h, NewParty(h));
        await h.AutoBuild.ApplyAsync(h.Scope, h.Ipswich.Id, Saturday, Saturday, ActingUser);

        await using (ApplicationDbContext ctx = new(h.Options))
        {
            Shift partyShift = await ctx.Shifts.SingleAsync(x => x.PartyBookingId == party.Id);
            partyShift.MarkPublished(Now, ActingUser);
            await ctx.SaveChangesAsync();
        }

        party.StartUtc = RotaTime.ToUtc(nextSaturday, new TimeOnly(10, 0));
        party.EndUtc = RotaTime.ToUtc(nextSaturday, new TimeOnly(12, 0));
        await BookAsync(h, party);

        // The published shift on the old day stands in for the party's Host, so only the old day is
        // flagged (change the shift's date) - and asking about the new day on its own agrees.
        string oldDay = PartyStaffingAlerts.DedupKey(h.Ipswich.Id, Saturday);
        CollectionAssert.AreEqual(new[] { oldDay }, (await h.OpenAlertsAsync()).Select(x => x.DedupKey).ToArray());

        await h.Watch.CheckDaysAsync(h.Ipswich.Id, [nextSaturday]);

        CollectionAssert.AreEqual(new[] { oldDay }, (await h.OpenAlertsAsync()).Select(x => x.DedupKey).ToArray());
    }
}
