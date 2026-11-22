using Lanyard.Application.Services;
using Lanyard.Application.Services.Checklists;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Application.Services.VideoStreaming;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Checklists;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
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
        UserProfile Amy);

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

        ChecklistRunService service = new(factory.Object, Mock.Of<IClockInTerminalService>(), Mock.Of<ITimeEntryService>(),
            new TerminalEphemeralTokenService(clock), tokens, clock, NullLogger<ChecklistRunService>.Instance);

        return new World(options, service, tokens, clock, company, ipswich, laser, otherKiosk, closing, card, amy);
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

        Result<bool> ticked = await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, card.Items[1].ItemId, null, null);
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

        Assert.IsFalse((await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, null, card.Items[0].ItemId, null, null)).IsSuccess);
        Assert.IsFalse((await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, "made-up", card.Items[0].ItemId, null, null)).IsSuccess);
        Assert.IsFalse((await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, otherToken, card.Items[0].ItemId, null, null)).IsSuccess);

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.AreEqual(0, await ctx.ChecklistCompletions.CountAsync());
    }

    [TestMethod]
    public async Task AnotherCompanysKiosk_CantTickIt()
    {
        World w = await SeedAsync();
        ChecklistRunView card = await CardAsync(w);
        string token = w.Tokens.IssueViewerToken(w.OtherCompanyKiosk.Id);

        Result<bool> result = await w.Service.CompleteOnKioskAsync(w.OtherCompanyKiosk.Id, token, card.Items[0].ItemId, null, null);

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
        Result<bool> result = await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, w.Tokens.IssueViewerToken(w.LaserKiosk.Id), card.Items[0].ItemId, null, null);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "can't be ticked on a kiosk");
    }

    [TestMethod]
    public async Task Undo_TakesBackAKioskTick_ButNotAPersons()
    {
        World w = await SeedAsync();
        string token = w.Tokens.IssueViewerToken(w.LaserKiosk.Id);
        ChecklistRunView card = await CardAsync(w);
        await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, card.Items[0].ItemId, null, null);

        // Amy's own tick on her phone (as a manager, so no rota needed).
        LocationScope amyManager = SchedulingTestHelpers.ManagerScopeFor(w.Ipswich);
        Assert.IsTrue((await w.Service.CompleteAsync(w.Amy.Id, amyManager, card.Items[1].ItemId, null, null)).IsSuccess);

        ChecklistRunView ticked = await CardAsync(w);

        Assert.IsTrue((await w.Service.UndoOnKioskAsync(w.LaserKiosk.Id, token, ticked.Items[0].Tick!.CompletionId)).IsSuccess);
        Result<bool> persons = await w.Service.UndoOnKioskAsync(w.LaserKiosk.Id, token, ticked.Items[1].Tick!.CompletionId);
        Assert.IsFalse(persons.IsSuccess);
    }

    [TestMethod]
    public async Task YesterdaysRun_OrFeatureOff_Refused()
    {
        World w = await SeedAsync();
        string token = w.Tokens.IssueViewerToken(w.LaserKiosk.Id);
        ChecklistRunView card = await CardAsync(w);

        w.Clock.Advance(TimeSpan.FromHours(3));
        Assert.IsFalse((await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, card.Items[0].ItemId, null, null)).IsSuccess);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = w.Company.Id, Feature = CompanyFeature.Checklists, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        ChecklistRunView today = (await w.Service.GetCardAsync(w.Closing.Id, w.Company.Id)).Data ?? card;
        Result<bool> off = await w.Service.CompleteOnKioskAsync(w.LaserKiosk.Id, token, today.Items[0].ItemId, null, null);
        Assert.IsFalse(off.IsSuccess);
    }
}
