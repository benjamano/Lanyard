using Lanyard.Application.Services;
using Lanyard.Application.Services.Checklists;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Application.Services.VideoStreaming;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Checklists;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Projection;
using Lanyard.Tests.Services.Scheduling;
using Lanyard.Tests.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Checklists;

// The Checklist card on a kiosk dashboard: nobody signs in, so ticks are made as the kiosk, proven
// by the viewer token it got over its own hub connection.
[TestClass]
public class ChecklistKioskCardTests
{
    // Thursday 19 Nov 2026, 21:30 GMT.
    private static readonly DateTime Now = new(2026, 11, 19, 21, 30, 0, DateTimeKind.Utc);

    private sealed record World(
        DbContextOptions<ApplicationDbContext> Options,
        ChecklistRunService Service,
        VideoStreamTokenService Tokens,
        TestClock Clock,
        Company Company,
        Location Ipswich,
        Client LaserKiosk,
        Client OtherCompanyKiosk,
        Checklist Closing,
        ChecklistCardWidget Card,
        UserProfile Amy,
        List<ProjectionRunState> Running,
        RecordingAlertWriter Alerts)
    {
        // The laser kiosk's display is on a Dashboard step for this dashboard (or, cleared, on nothing).
        public void ShowOnLaser(Guid dashboardId) => Running.Add(DashboardRun(LaserKiosk.Id, dashboardId));
    }

    private static ProjectionRunState DashboardRun(Guid clientId, Guid dashboardId) => new()
    {
        RunId = Guid.NewGuid(),
        ClientId = clientId,
        DisplayIndex = 0,
        ProgramId = Guid.NewGuid(),
        ProgramName = "Laser screen",
        CurrentStepIndex = 0,
        IsPaused = false,
        IsTemporaryTrigger = false,
        Steps =
        [
            new ProjectionProgramStep
            {
                Template = new ProjectionProgramStepTemplate { Name = ProjectionTemplateNames.Dashboard },
                ParameterValues =
                [
                    new ProjectionProgramParameterValue
                    {
                        Parameter = new ProjectionProgramStepTemplateParameter { TemplateId = Guid.Empty, Name = ProjectionParameterNames.Dashboard, DataType = "string" },
                        Value = dashboardId.ToString()
                    }
                ]
            }
        ]
    };

    private static async Task<World> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (Company other, _) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Partyman", "Peterborough");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy");

        Client laser = new() { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Laser" };
        Client otherKiosk = new() { Id = Guid.NewGuid(), CompanyId = other.Id, Name = "Their kiosk" };

        Checklist closing = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            LocationId = ipswich.Id,
            Name = "Laser closing",
            CreateDate = Now.AddDays(-3),
            Tasks =
            [
                new ChecklistTask { ChecklistId = Guid.Empty, Title = "Turn off all lights", SortOrder = 0 },
                new ChecklistTask { ChecklistId = Guid.Empty, Title = "Plug in all guns", SortOrder = 1 },
                new ChecklistTask { ChecklistId = Guid.Empty, Title = "Fog the building", SortOrder = 2 }
            ]
        };

        Dashboard dashboard = new() { Id = Guid.NewGuid(), CompanyId = company.Id, Name = "Laser", IsActive = true, CreateDate = Now };
        ChecklistCardWidget card = new() { DashboardId = dashboard.Id, ChecklistCardChecklistId = closing.Id, ChecklistCardAllowKioskTicking = true };

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Clients.AddRange(laser, otherKiosk);
            ctx.Checklists.Add(closing);
            ctx.Dashboards.Add(dashboard);
            ctx.DashboardWidgets.Add(card);
            await ctx.SaveChangesAsync();
        }

        TestClock clock = new(Now);
        VideoStreamTokenService tokens = new();

        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));

        List<ProjectionRunState> running = [DashboardRun(laser.Id, dashboard.Id)];
        Mock<IProjectionProgramRunnerService> runner = new();
        runner.Setup(x => x.GetRunningStates()).Returns(() => [.. running]);

        WidgetVisibilityService visibility = new(new SystemDbContextFactory(options), Mock.Of<ILaserGameStatusStore>(), NullLogger<WidgetVisibilityService>.Instance);

        RecordingAlertWriter alerts = new();
        ChecklistRunService service = new(factory.Object, Mock.Of<IClockInTerminalService>(), Mock.Of<ITimeEntryService>(),
            new TerminalEphemeralTokenService(clock), tokens, runner.Object, visibility, alerts, clock, NullLogger<ChecklistRunService>.Instance);

        return new World(options, service, tokens, clock, company, ipswich, laser, otherKiosk, closing, card, amy, running, alerts);
    }

    private static async Task<ChecklistRunView> CardAsync(World w)
    {
        Result<ChecklistRunView?> card = await w.Service.GetCardAsync(w.Closing.Id, w.Company.Id);
        Assert.IsTrue(card.IsSuccess, card.Error);
        Assert.IsNotNull(card.Data);

        return card.Data;
    }

    [TestMethod]
    public async Task GetCardAsync_ReadsTodaysRun_ForTheDashboardsCompanyOnly()
    {
        World w = await SeedAsync();

        ChecklistRunView card = await CardAsync(w);
        Assert.AreEqual(3, card.Total);
        Assert.IsFalse(card.CanTick, "Reading the card never grants ticking; the kiosk path does that.");

        Result<ChecklistRunView?> otherCompany = await w.Service.GetCardAsync(w.Closing.Id, w.Company.Id + 1);
        Assert.IsFalse(otherCompany.IsSuccess);
    }

    [TestMethod]
    public async Task KioskTick_IsRecordedAsTheKiosk_WithNoPerson()
    {
        World w = await SeedAsync();
        string token = w.Tokens.IssueViewerToken(w.LaserKiosk.Id);
        ChecklistRunView card = await CardAsync(w);

        Result<bool> ticked = await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, card.Items[1].ItemId, null, null);
        Assert.IsTrue(ticked.IsSuccess, ticked.Error);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ChecklistCompletion tick = await ctx.ChecklistCompletions.SingleAsync();
            Assert.IsNull(tick.UserId);
            Assert.AreEqual(w.LaserKiosk.Id, tick.ClientId);
            Assert.AreEqual(ChecklistCompletionMethod.Kiosk, tick.Method);
        }

        ChecklistRunView after = await CardAsync(w);
        Assert.AreEqual("Laser", after.Items[1].Tick!.Name, "Shown as the kiosk's name.");
        Assert.AreEqual(ChecklistRunStatus.InProgress, after.Status);
    }

    [TestMethod]
    public async Task WrongMissingOrAnotherKiosksToken_IsRefused()
    {
        World w = await SeedAsync();
        ChecklistRunView card = await CardAsync(w);
        string otherToken = w.Tokens.IssueViewerToken(w.OtherCompanyKiosk.Id);

        Assert.IsFalse((await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, null, w.Card.Id, card.Items[0].ItemId, null, null)).IsSuccess);
        Assert.IsFalse((await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, "made-up", w.Card.Id, card.Items[0].ItemId, null, null)).IsSuccess);
        Assert.IsFalse((await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, otherToken, w.Card.Id, card.Items[0].ItemId, null, null)).IsSuccess);

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.AreEqual(0, await ctx.ChecklistCompletions.CountAsync());
    }

    [TestMethod]
    public async Task CanTickOnKiosk_OnlyWithTheKiosksOwnTokenAndAnAllowingCard()
    {
        World w = await SeedAsync();
        string token = w.Tokens.IssueViewerToken(w.LaserKiosk.Id);

        Assert.IsTrue((await w.Service.CanTickOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id)).Data);
        Assert.IsFalse((await w.Service.CanTickOnKioskAsync(w.LaserKiosk.Id, "not-the-kiosk", w.Card.Id)).Data);
        Assert.IsFalse((await w.Service.CanTickOnKioskAsync(w.OtherCompanyKiosk.Id, w.Tokens.IssueViewerToken(w.OtherCompanyKiosk.Id), w.Card.Id)).Data);
    }

    [TestMethod]
    public async Task AnotherCompanysKiosk_CantTickIt()
    {
        World w = await SeedAsync();
        ChecklistRunView card = await CardAsync(w);
        string token = w.Tokens.IssueViewerToken(w.OtherCompanyKiosk.Id);

        Result<bool> result = await w.Service.CompleteOnKioskAsync(w.OtherCompanyKiosk.Id, token, w.Card.Id, card.Items[0].ItemId, null, null);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "wasn't found");
    }

    [TestMethod]
    public async Task NoCardAllowsKioskTicking_Refused()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            (await ctx.DashboardWidgets.OfType<ChecklistCardWidget>().SingleAsync()).ChecklistCardAllowKioskTicking = false;
            await ctx.SaveChangesAsync();
        }

        ChecklistRunView card = await CardAsync(w);
        Result<bool> result = await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, w.Tokens.IssueViewerToken(w.LaserKiosk.Id), w.Card.Id, card.Items[0].ItemId, null, null);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "can't be ticked on this screen");
    }

    [TestMethod]
    public async Task Undo_TakesBackAKioskTick_ButNotAPersons()
    {
        World w = await SeedAsync();
        string token = w.Tokens.IssueViewerToken(w.LaserKiosk.Id);
        ChecklistRunView card = await CardAsync(w);
        await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, card.Items[0].ItemId, null, null);

        // Amy's own tick on her phone (as a manager, so no rota needed).
        LocationScope amyManager = SchedulingTestHelpers.ManagerScopeFor(w.Ipswich);
        Assert.IsTrue((await w.Service.CompleteAsync(w.Amy.Id, amyManager, card.Items[1].ItemId, null, null)).IsSuccess);

        ChecklistRunView ticked = await CardAsync(w);

        Assert.IsTrue((await w.Service.UndoOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, ticked.Items[0].Tick!.CompletionId)).IsSuccess);
        Result<bool> persons = await w.Service.UndoOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, ticked.Items[1].Tick!.CompletionId);
        Assert.IsFalse(persons.IsSuccess);
    }

    [TestMethod]
    public async Task YesterdaysRun_OrFeatureOff_Refused()
    {
        World w = await SeedAsync();
        string token = w.Tokens.IssueViewerToken(w.LaserKiosk.Id);
        ChecklistRunView card = await CardAsync(w);

        w.Clock.Advance(TimeSpan.FromHours(3));
        Assert.IsFalse((await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, card.Items[0].ItemId, null, null)).IsSuccess);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            LocationFeatureSetting checklists = await ctx.LocationFeatureSettings
                .SingleAsync(x => x.LocationId == w.Ipswich.Id && x.Feature == CompanyFeature.Checklists);
            checklists.IsEnabled = false;
            await ctx.SaveChangesAsync();
        }

        ChecklistRunView today = (await w.Service.GetCardAsync(w.Closing.Id, w.Company.Id)).Data ?? card;
        Result<bool> off = await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, today.Items[0].ItemId, null, null);
        Assert.IsFalse(off.IsSuccess);
    }

    [TestMethod]
    public async Task AKioskNotShowingTheCardsDashboard_CantTick()
    {
        World w = await SeedAsync();
        string token = w.Tokens.IssueViewerToken(w.LaserKiosk.Id);
        ChecklistRunView card = await CardAsync(w);

        // The laser kiosk has moved on to another dashboard.
        w.Running.Clear();
        w.ShowOnLaser(Guid.NewGuid());

        Assert.IsFalse((await w.Service.CanTickOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id)).Data);
        Result<bool> result = await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, card.Items[0].ItemId, null, null);
        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "isn't showing");
    }

    [TestMethod]
    public async Task ACardOutsideItsShowOnlyWhenTimes_CantTick()
    {
        World w = await SeedAsync();
        string token = w.Tokens.IssueViewerToken(w.LaserKiosk.Id);
        ChecklistRunView card = await CardAsync(w);

        // Shown 06:00-08:00 only; it's 21:30.
        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ChecklistCardWidget stored = await ctx.DashboardWidgets.OfType<ChecklistCardWidget>().SingleAsync();
            stored.ShowConditionsJson = WidgetShowConditions.Serialize(
            [
                new AutomationRuleCondition
                {
                    Id = Guid.NewGuid(),
                    ConditionType = AutomationConditionType.TimeWindow,
                    ParametersJson = AutomationConditionParameters.Serialize(new TimeWindowConditionParameters(new TimeOnly(6, 0), new TimeOnly(8, 0))),
                    IsActive = true
                }
            ]);
            await ctx.SaveChangesAsync();
        }

        Result<bool> result = await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, card.Items[0].ItemId, null, null);
        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "isn't shown");
    }

    [TestMethod]
    public async Task AReadOnlyCardElsewhere_DoesntOpenTicking()
    {
        // Ticking is allowed on the laser dashboard's card, but the reception kiosk shows a read-only
        // card for the same checklist: it must not tick, through its own card or by borrowing the other.
        World w = await SeedAsync();
        Client reception = new() { Id = Guid.NewGuid(), CompanyId = w.Company.Id, Name = "Reception" };
        Dashboard receptionDashboard = new() { Id = Guid.NewGuid(), CompanyId = w.Company.Id, Name = "Reception", IsActive = true, CreateDate = Now };
        ChecklistCardWidget readOnly = new() { DashboardId = receptionDashboard.Id, ChecklistCardChecklistId = w.Closing.Id, ChecklistCardAllowKioskTicking = false };

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.Clients.Add(reception);
            ctx.Dashboards.Add(receptionDashboard);
            ctx.DashboardWidgets.Add(readOnly);
            await ctx.SaveChangesAsync();
        }

        w.Running.Add(DashboardRun(reception.Id, receptionDashboard.Id));
        string token = w.Tokens.IssueViewerToken(reception.Id);
        ChecklistRunView card = await CardAsync(w);

        Assert.IsFalse((await w.Service.CompleteOnKioskAsync(reception.Id, token, readOnly.Id, card.Items[0].ItemId, null, null)).IsSuccess);
        Assert.IsFalse((await w.Service.CompleteOnKioskAsync(reception.Id, token, w.Card.Id, card.Items[0].ItemId, null, null)).IsSuccess,
            "The laser card isn't on reception's screen.");
    }

    // ---- Readings outside the task's acceptable range, ticked on a kiosk ----

    [TestMethod]
    public async Task KioskTick_OutOfRange_RaisesAsTheKiosk_WithNoPerson()
    {
        World w = await SeedAsync();
        await ChecklistReadingTestSupport.SetRangeAsync(w.Options, "Fog the building", null, 40m, unit: "%");
        string token = w.Tokens.IssueViewerToken(w.LaserKiosk.Id);
        ChecklistItemView fog = (await CardAsync(w)).Items.Single(x => x.Title == "Fog the building");

        Result<bool> ticked = await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, fog.ItemId, null, 55m);
        Assert.IsTrue(ticked.IsSuccess, ticked.Error);

        OperationsAlertRaise raise = w.Alerts.Raised.Single();
        Assert.AreEqual(w.Company.Id, raise.CompanyId);
        Assert.AreEqual(w.Ipswich.Id, raise.LocationId);
        Assert.IsNull(raise.SubjectUserId);
        Assert.AreEqual(w.LaserKiosk.Id, raise.SubjectClientId);
        Assert.AreEqual("Fog the building out of range: 55 %", raise.Title);
        StringAssert.Contains(raise.Detail, "by Laser");
    }

    [TestMethod]
    public async Task KioskUndo_LeavesTheAlertOpen_AndAnInRangeReTickClearsIt()
    {
        World w = await SeedAsync();
        await ChecklistReadingTestSupport.SetRangeAsync(w.Options, "Fog the building", null, 40m, unit: "%");
        string token = w.Tokens.IssueViewerToken(w.LaserKiosk.Id);
        ChecklistItemView fog = (await CardAsync(w)).Items.Single(x => x.Title == "Fog the building");

        Assert.IsTrue((await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, fog.ItemId, null, 55m)).IsSuccess);
        Guid completionId = (await CardAsync(w)).Items.Single(x => x.ItemId == fog.ItemId).Tick!.CompletionId;

        Assert.IsTrue((await w.Service.UndoOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, completionId)).IsSuccess);
        Assert.AreEqual(0, w.Alerts.Resolved.Count);

        Assert.IsTrue((await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id, fog.ItemId, null, 30m)).IsSuccess);
        Assert.AreEqual(ChecklistReadingTestSupport.KeyFor(fog.ItemId), w.Alerts.Resolved.Single().Key);
        StringAssert.Contains(w.Alerts.Resolved.Single().Note, "(Laser)");
    }

    // Checklists are switched on per location. The kiosk path used to check the company's id as if
    // it were a location's, so a kiosk ticking another location's checklist depended on whichever
    // location happened to share the company's number.
    [TestMethod]
    public async Task KioskTick_ChecksTheChecklistsOwnLocation_NotTheLocationWithTheCompanysId()
    {
        World w = await SeedAsync();
        Assert.AreEqual(w.Company.Id, w.Ipswich.Id, "The test needs a location numbered like the company.");

        Location wisbech = new() { CompanyId = w.Company.Id, Name = "Wisbech", IsActive = true };
        Checklist wisbechClosing = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = w.Company.Id,
            LocationId = 0,
            Name = "Wisbech closing",
            CreateDate = Now.AddDays(-3),
            Tasks = [new ChecklistTask { ChecklistId = Guid.Empty, Title = "Lock up", SortOrder = 0 }]
        };
        ChecklistCardWidget wisbechCard = new() { DashboardId = w.Card.DashboardId, ChecklistCardChecklistId = wisbechClosing.Id, ChecklistCardAllowKioskTicking = true };

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.Locations.Add(wisbech);
            await ctx.SaveChangesAsync();
            wisbechClosing.LocationId = wisbech.Id;
            ctx.Checklists.Add(wisbechClosing);
            ctx.DashboardWidgets.Add(wisbechCard);
            await ctx.SaveChangesAsync();
            await TestFeatures.SwitchOnEverywhereAsync(ctx);

            // Checklists off at Ipswich only - the location whose number matches the company's.
            ctx.LocationFeatureSettings.RemoveRange(ctx.LocationFeatureSettings.Where(x => x.LocationId == w.Ipswich.Id && x.Feature == CompanyFeature.Checklists));
            await ctx.SaveChangesAsync();
        }

        string token = w.Tokens.IssueViewerToken(w.LaserKiosk.Id);
        Result<ChecklistRunView?> card = await w.Service.GetCardAsync(wisbechClosing.Id, w.Company.Id);
        Assert.IsTrue(card.IsSuccess, card.Error);

        Result<bool> ticked = await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, wisbechCard.Id, card.Data!.Items[0].ItemId, null, null);
        Assert.IsTrue(ticked.IsSuccess, ticked.Error);

        // And Ipswich's own checklist, with the feature off there, is refused.
        Assert.IsFalse((await w.Service.GetCardAsync(w.Closing.Id, w.Company.Id)).IsSuccess, "Checklists are off at Ipswich.");

        Result<bool> canTick = await w.Service.CanTickOnKioskAsync(w.LaserKiosk.Id, token, w.Card.Id);
        Assert.IsTrue(canTick.IsSuccess);
        Assert.IsFalse(canTick.Data);
    }
}
