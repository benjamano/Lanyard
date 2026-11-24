using Lanyard.Tests.TestUtilities;
using Lanyard.Application.Services.Insights;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Insights;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace Lanyard.Tests.Services.Insights;

[TestClass]
public class DemandInsightsServiceTests
{
    // Friday 16 October 2026, midday UK time.
    private static readonly DateTime Now = new(2026, 10, 16, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static readonly InsightsQuery Week = new(null, Monday, Monday.AddDays(6));

    private sealed class TestTenant(int? companyId, bool isSystem = false) : ITenantContext
    {
        public bool IsSystem => isSystem;
        public int? CompanyId => isSystem ? null : companyId;
        public bool IsPlatformAdmin => false;
        public int? ManageableCompanyId => isSystem ? null : companyId ?? -1;
        public bool CanManageCompany(int id) => ManageableCompanyId is null || ManageableCompanyId == id;
    }

    private record Fixture(
        DbContextOptions<ApplicationDbContext> Options,
        Company Company,
        Location Ipswich,
        Location Wisbech,
        Company OtherCompany,
        Location OtherLocation,
        Guid IpswichKiosk,
        Guid WisbechKiosk,
        Guid UnassignedKiosk,
        Guid OtherCompanyKiosk);

    private static async Task<Fixture> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Play2Day", "Ipswich");
        (Company otherCompany, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Location wisbech;
        Guid ipswichKiosk = Guid.NewGuid(), wisbechKiosk = Guid.NewGuid(), unassignedKiosk = Guid.NewGuid(), otherKiosk = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            wisbech = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(wisbech);
            await ctx.SaveChangesAsync();

            ctx.Clients.AddRange(
                new Client { Id = ipswichKiosk, CompanyId = company.Id, Name = "Ipswich arena", LocationId = ipswich.Id },
                new Client { Id = wisbechKiosk, CompanyId = company.Id, Name = "Wisbech arena", LocationId = wisbech.Id },
                new Client { Id = unassignedKiosk, CompanyId = company.Id, Name = "Spare kiosk" },
                // Another company's kiosk claiming Ipswich's location id must never count.
                new Client { Id = otherKiosk, CompanyId = otherCompany.Id, Name = "Their arena", LocationId = ipswich.Id });
            await ctx.SaveChangesAsync();
        }

        return new Fixture(options, company, ipswich, wisbech, otherCompany, otherLocation, ipswichKiosk, wisbechKiosk, unassignedKiosk, otherKiosk);
    }

    private static DemandInsightsService ServiceFor(Fixture f, ITenantContext? tenant = null) =>
        new(SchedulingTestHelpers.GetFactory(f.Options), tenant ?? new TestTenant(f.Company.Id), new TestClock(Now), NullLogger<DemandInsightsService>.Instance);

    private static LocationScope AdminScope(Fixture f) => new(true, f.Ipswich.Id, f.Company.Id, "Play2Day Ipswich");

    private static DateTime Local(DateOnly day, int hour, int minute = 0) => RotaTime.ToUtc(day, new TimeOnly(hour, minute));

    private static async Task AddGameAsync(Fixture f, Guid clientId, DateTime playedAtUtc, int players = 6, int? companyId = null)
    {
        await using ApplicationDbContext ctx = new(f.Options);
        ctx.GameResults.Add(new GameResult
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId ?? f.Company.Id,
            ClientId = clientId,
            PlayedAtUtc = playedAtUtc,
            DurationSeconds = 600,
            PlayerScores = [.. Enumerable.Range(1, players).Select(gun => new GameResultPlayerScore { Id = Guid.NewGuid(), GunId = gun, Score = 100 })]
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task AddEntryAsync(Fixture f, Location location, DateTime inUtc, DateTime? outUtc, bool isActive = true)
    {
        await using ApplicationDbContext ctx = new(f.Options);
        ctx.TimeEntries.Add(new TimeEntry
        {
            Id = Guid.NewGuid(),
            UserId = "amy",
            LocationId = location.Id,
            ClockInUtc = inUtc,
            ClockOutUtc = outUtc,
            IsActive = isActive,
            CreateByUserId = "amy"
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task AddShiftAsync(Fixture f, Location location, DateTime startUtc, DateTime endUtc, bool published = true, bool isActive = true)
    {
        await using ApplicationDbContext ctx = new(f.Options);
        ctx.Shifts.Add(new Shift
        {
            Id = Guid.NewGuid(),
            LocationId = location.Id,
            UserId = "amy",
            StartUtc = startUtc,
            EndUtc = endUtc,
            PublishedDateUtc = published ? startUtc.AddDays(-7) : null,
            IsActive = isActive,
            CreateByUserId = "manager",
            CreateDate = startUtc.AddDays(-8)
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<PartyBooking> AddPartyAsync(Fixture f, Location location, DateTime startUtc, DateTime endUtc,
        DateTime? laserUtc = null, int children = 12, PartyBookingStatus status = PartyBookingStatus.Booked,
        bool provisional = false, int? companyId = null, PrivateHireKind? hire = null)
    {
        await using ApplicationDbContext ctx = new(f.Options);
        PartyBooking party = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId ?? f.Company.Id,
            LocationId = location.Id,
            StartUtc = startUtc,
            EndUtc = endUtc,
            LaserTagTimeUtc = laserUtc,
            HireKind = hire,
            PartyType = "Lazer + Play",
            ChildName = "Ava Testchild",
            ContactName = "Morgan Parent",
            ContactPhone = "07700 900123",
            ContactEmail = "morgan.parent@example.test",
            AllergyNotes = "Severe peanut allergy",
            Notes = "Wants the blue room",
            TotalPrice = 249.99m,
            DepositAmount = 50m,
            ExpectedChildren = children,
            ExpectedAdults = 6,
            Status = status,
            ExternalIsProvisional = provisional,
            CreateByUserId = "manager",
            CreateDate = startUtc.AddDays(-20)
        };
        ctx.PartyBookings.Add(party);
        await ctx.SaveChangesAsync();
        return party;
    }

    private static async Task SwitchOffAsync(Fixture f, CompanyFeature feature)
    {
        await using ApplicationDbContext ctx = new(f.Options);
        await TestFeatures.SwitchOffForCompanyAsync(ctx, f.Company.Id, feature);
        await ctx.SaveChangesAsync();
    }

    // ---- Refusals ----

    [TestMethod]
    public async Task GetSummary_FailsForASystemCaller()
    {
        Fixture f = await SeedAsync();
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14));

        Result<InsightsSummary> result = await ServiceFor(f, new TestTenant(null, isSystem: true)).GetSummaryAsync(AdminScope(f), Week);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task GetSummary_FailsForAPlainStaffMember()
    {
        Fixture f = await SeedAsync();

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(SchedulingTestHelpers.StaffScopeFor(f.Ipswich), Week);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetSummary_FailsForAnotherCompanysLocation()
    {
        Fixture f = await SeedAsync();

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week with { LocationId = f.OtherLocation.Id });

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetSummary_FailsForABackwardsRange()
    {
        Fixture f = await SeedAsync();

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), new InsightsQuery(null, Monday, Monday.AddDays(-1)));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetSummary_FailsForARangeOverTheLimit()
    {
        Fixture f = await SeedAsync();
        DateOnly to = RotaTime.Today(Now);

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), new InsightsQuery(null, to.AddDays(-InsightsQuery.MaxRangeDays), to));

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, InsightsQuery.MaxRangeDays.ToString());
    }

    [TestMethod]
    public async Task GetSummary_AllowsARangeOfExactlyTheLimit()
    {
        Fixture f = await SeedAsync();
        DateOnly to = RotaTime.Today(Now);

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), new InsightsQuery(null, to.AddDays(1 - InsightsQuery.MaxRangeDays), to));

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task GetSummary_FailsForARangeEndingInTheFuture()
    {
        Fixture f = await SeedAsync();
        DateOnly today = RotaTime.Today(Now);

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), new InsightsQuery(null, today, today.AddDays(1)));

        Assert.IsFalse(result.IsSuccess);
    }

    // ---- Which location ----

    [TestMethod]
    public async Task GetSummary_PinsAManagerToTheirOwnLocation()
    {
        Fixture f = await SeedAsync();
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14));
        await AddGameAsync(f, f.WisbechKiosk, Local(Monday, 15));
        await AddGameAsync(f, f.WisbechKiosk, Local(Monday, 16));

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(
            SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), Week with { LocationId = f.Wisbech.Id });

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(f.Ipswich.Id, result.Data!.LocationId);
        Assert.AreEqual(1, result.Data.Figures.Totals.Games);
    }

    [TestMethod]
    public async Task GetSummary_LetsAnAdminPickAnyLocationOfTheirCompany()
    {
        Fixture f = await SeedAsync();
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14));
        await AddGameAsync(f, f.WisbechKiosk, Local(Monday, 15));
        await AddGameAsync(f, f.WisbechKiosk, Local(Monday, 16));

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week with { LocationId = f.Wisbech.Id });

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(f.Wisbech.Id, result.Data!.LocationId);
        Assert.AreEqual(2, result.Data.Figures.Totals.Games);
    }

    // ---- What counts ----

    [TestMethod]
    public async Task GetSummary_CountsOnlyGamesFromThisLocationsKiosks()
    {
        Fixture f = await SeedAsync();
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14), players: 8);
        await AddGameAsync(f, f.WisbechKiosk, Local(Monday, 14));
        await AddGameAsync(f, f.UnassignedKiosk, Local(Monday, 14));
        await AddGameAsync(f, f.OtherCompanyKiosk, Local(Monday, 14), companyId: f.OtherCompany.Id);
        // Played on Ipswich's kiosk id but stamped with another company: refused.
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 15), companyId: f.OtherCompany.Id);
        // Outside the range.
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday.AddDays(-1), 14));

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.Data!.Figures.Totals.Games);
        Assert.AreEqual(8, result.Data.Figures.Totals.Players);
        Assert.AreEqual(1, result.Data.KioskCount);
        Assert.AreEqual(1, result.Data.UnassignedKioskCount);
    }

    [TestMethod]
    public async Task GetSummary_CountsActiveClockedTimeAtThisLocation()
    {
        Fixture f = await SeedAsync();
        await AddEntryAsync(f, f.Ipswich, Local(Monday, 9), Local(Monday, 17));
        await AddEntryAsync(f, f.Ipswich, Local(Monday, 9), Local(Monday, 17), isActive: false);
        await AddEntryAsync(f, f.Wisbech, Local(Monday, 9), Local(Monday, 17));

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(8m, result.Data!.Figures.Totals.ClockedHours);
    }

    [TestMethod]
    public async Task GetSummary_RunsAnOpenEntryUntilNow()
    {
        Fixture f = await SeedAsync();
        DateOnly today = RotaTime.Today(Now);
        await AddEntryAsync(f, f.Ipswich, Now.AddHours(-3), null);

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), new InsightsQuery(null, today, today));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(3m, result.Data!.Figures.Totals.ClockedHours);
    }

    [TestMethod]
    public async Task GetSummary_CountsOnlyActivePublishedShifts()
    {
        Fixture f = await SeedAsync();
        await AddShiftAsync(f, f.Ipswich, Local(Monday, 10), Local(Monday, 16));
        await AddShiftAsync(f, f.Ipswich, Local(Monday, 10), Local(Monday, 16), published: false);
        await AddShiftAsync(f, f.Ipswich, Local(Monday, 10), Local(Monday, 16), isActive: false);
        await AddShiftAsync(f, f.Wisbech, Local(Monday, 10), Local(Monday, 16));

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(6m, result.Data!.Figures.Totals.RotaHours);
    }

    [TestMethod]
    public async Task GetSummary_UsesThisLocationsOpeningHours()
    {
        Fixture f = await SeedAsync();

        await using (ApplicationDbContext ctx = new(f.Options))
        {
            ctx.LocationOpeningHours.AddRange(
                new LocationOpeningHours { LocationId = f.Ipswich.Id, DayOfWeek = DayOfWeek.Monday, OpensAt = new TimeOnly(10, 0), ClosesAt = new TimeOnly(18, 0), IsActive = true },
                new LocationOpeningHours { LocationId = f.Wisbech.Id, DayOfWeek = DayOfWeek.Tuesday, OpensAt = new TimeOnly(10, 0), ClosesAt = new TimeOnly(18, 0), IsActive = true });
            await ctx.SaveChangesAsync();
        }

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Data!.HasOpeningHours);
        Assert.AreEqual(8, result.Data.Figures.Days.Sum(x => x.OpenHours));
        Assert.IsTrue(result.Data.Figures.Slots.All(x => x.Date.DayOfWeek == DayOfWeek.Monday));
    }

    [TestMethod]
    public async Task GetSummary_ReturnsZeroesForAnEmptyRange()
    {
        Fixture f = await SeedAsync();

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, result.Data!.Figures.Totals.Games);
        Assert.AreEqual(0m, result.Data.Figures.Totals.ClockedHours);
        Assert.IsNull(result.Data.Figures.Totals.LabourHoursPerGame);
        Assert.IsFalse(result.Data.Figures.FlagsAvailable);
        Assert.AreEqual(7, result.Data.Figures.Days.Count);
        Assert.IsFalse(result.Data.HasOpeningHours);
    }

    // ---- Export ----

    [TestMethod]
    public async Task ExportCsv_WritesOneRowPerBusyHourWithNoNames()
    {
        Fixture f = await SeedAsync();
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14, 30));
        await AddEntryAsync(f, f.Ipswich, Local(Monday, 14), Local(Monday, 16));

        Result<InsightsExportFile> result = await ServiceFor(f).ExportCsvAsync(AdminScope(f), Week);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("text/csv", result.Data!.ContentType);
        StringAssert.EndsWith(result.Data.FileName, ".csv");

        string csv = Encoding.UTF8.GetString(result.Data.Content).TrimStart('﻿');
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.AreEqual(string.Join(',', [.. InsightsExportBuilder.Header, .. InsightsExportBuilder.PartyHeader]), lines[0]);
        Assert.AreEqual(3, lines.Length);
        Assert.AreEqual("2026-10-05,Monday,14:00,Yes,,1,6,1.00,0.00,1.00,1,0,0,0", lines[1]);
        Assert.IsFalse(csv.Contains("amy", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ExportCsv_FailsWhenTheSummaryIsRefused()
    {
        Fixture f = await SeedAsync();

        Result<InsightsExportFile> result = await ServiceFor(f, new TestTenant(null, isSystem: true)).ExportCsvAsync(AdminScope(f), Week);

        Assert.IsFalse(result.IsSuccess);
    }

    // ---- Parties ----

    [TestMethod]
    public async Task GetSummary_CountsOnlyBookedPartiesAtThisLocationOfThisCompany()
    {
        Fixture f = await SeedAsync();
        await AddPartyAsync(f, f.Ipswich, Local(Monday, 13), Local(Monday, 15), children: 12);
        await AddPartyAsync(f, f.Ipswich, Local(Monday, 16), Local(Monday, 18), children: 30, status: PartyBookingStatus.Cancelled);
        await AddPartyAsync(f, f.Wisbech, Local(Monday, 13), Local(Monday, 15), children: 40);
        // Another company's booking claiming Ipswich's location id must never count.
        await AddPartyAsync(f, f.Ipswich, Local(Monday, 13), Local(Monday, 15), children: 50, companyId: f.OtherCompany.Id);

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Data!.HasPartyData);
        Assert.AreEqual(1, result.Data.Figures.Totals.Parties);
        Assert.AreEqual(12, result.Data.Figures.Totals.ExpectedChildren);
        Assert.AreEqual(12, result.Data.Figures.Slots.Single(x => x.Date == Monday && x.Hour == 14).ExpectedChildren);
        Assert.IsFalse(result.Data.Figures.Slots.Any(x => x.Hour == 16 && x.Parties > 0));
    }

    [TestMethod]
    public async Task GetSummary_CountsProvisionalImportedParties()
    {
        Fixture f = await SeedAsync();
        await AddPartyAsync(f, f.Ipswich, Local(Monday, 13), Local(Monday, 15), provisional: true);

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.AreEqual(1, result.Data!.Figures.Totals.Parties);
    }

    [TestMethod]
    public async Task GetSummary_PutsAGameAtTheLaserTimeDownToTheParty()
    {
        Fixture f = await SeedAsync();
        await AddPartyAsync(f, f.Ipswich, Local(Monday, 13), Local(Monday, 15), laserUtc: Local(Monday, 14));
        // Recorded ten minutes after they start (DurationSeconds = 600): 14:05 is the party's, and
        // 14:50 is too long after it to be its second game.
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14, 15));
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 15, 0));

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.AreEqual(2, result.Data!.Figures.Totals.Games);
        Assert.AreEqual(1, result.Data.Figures.Totals.PartyGames);
        Assert.AreEqual(1, result.Data.Figures.Totals.WalkInGames);
    }

    [TestMethod]
    public async Task GetSummary_GivesAPartyItsTwoGamesAndNoMore()
    {
        Fixture f = await SeedAsync();
        await AddPartyAsync(f, f.Ipswich, Local(Monday, 13), Local(Monday, 15), laserUtc: Local(Monday, 14));
        // Games starting 14:00, 14:14 and 14:28, back to back.
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14, 10));
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14, 24));
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14, 38));

        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.AreEqual(2, result.Data!.Figures.Totals.PartyGames);
        Assert.AreEqual(1, result.Data.Figures.Totals.WalkInGames);
    }

    [TestMethod]
    public async Task GetSummary_UsesTheLocationsLaserOffsetForPartiesWithoutATime()
    {
        Fixture f = await SeedAsync();
        await AddPartyAsync(f, f.Ipswich, Local(Monday, 13), Local(Monday, 15));
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14, 10));

        Result<InsightsSummary> before = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.AreEqual(0, before.Data!.Figures.Totals.PartyGames);
        Assert.AreEqual(1, before.Data.Figures.Totals.PartiesWithoutLaserTime);

        await using (ApplicationDbContext ctx = new(f.Options))
        {
            ctx.PartyLocationSettings.Add(new PartyLocationSettings { Id = Guid.NewGuid(), LocationId = f.Ipswich.Id, LaserTagOffsetMinutes = 60 });
            await ctx.SaveChangesAsync();
        }

        Result<InsightsSummary> after = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.AreEqual(1, after.Data!.Figures.Totals.PartyGames);
        Assert.AreEqual(0, after.Data.Figures.Totals.PartiesWithoutLaserTime);
    }

    [TestMethod]
    public async Task GetSummary_LeavesPartiesOutWhenTheCompanyDoesNotUseThem()
    {
        Fixture f = await SeedAsync();
        await SwitchOffAsync(f, CompanyFeature.Parties);
        await AddPartyAsync(f, f.Ipswich, Local(Monday, 13), Local(Monday, 15), laserUtc: Local(Monday, 14));
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14, 15));

        // An Admin too: the server follows the company's switch, as the rota auto-build does.
        Result<InsightsSummary> result = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse(result.Data!.HasPartyData);
        Assert.AreEqual(0, result.Data.Figures.Totals.Parties);
        Assert.AreEqual(0, result.Data.Figures.Totals.PartyGames);
        Assert.AreEqual(1, result.Data.Figures.Totals.Games);
        Assert.IsTrue(result.Data.Figures.Cells.All(x => x.RateSamples == x.Samples));
    }

    [TestMethod]
    public async Task GetSummary_RefusesAManagerWhenInsightsIsSwitchedOff_ButNotAnAdmin()
    {
        Fixture f = await SeedAsync();
        await SwitchOffAsync(f, CompanyFeature.Insights);

        Result<InsightsSummary> manager = await ServiceFor(f).GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), Week);
        Result<InsightsSummary> admin = await ServiceFor(f).GetSummaryAsync(AdminScope(f), Week);

        Assert.IsFalse(manager.IsSuccess);
        Assert.IsTrue(admin.IsSuccess, admin.Error);
    }

    [TestMethod]
    public async Task ExportCsv_CarriesPartyCountsButNothingAboutWhoOrMoney()
    {
        Fixture f = await SeedAsync();
        await AddPartyAsync(f, f.Ipswich, Local(Monday, 13), Local(Monday, 15), laserUtc: Local(Monday, 14), children: 14);
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14, 15));

        Result<InsightsExportFile> result = await ServiceFor(f).ExportCsvAsync(AdminScope(f), Week);

        Assert.IsTrue(result.IsSuccess, result.Error);
        string csv = Encoding.UTF8.GetString(result.Data!.Content).TrimStart('\uFEFF');
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.AreEqual("Date,Day,Hour,Open,Part of day,Games,Players,Clocked hours,Rota hours,Games per staff hour,"
            + "Walk-in games,Party games,Parties on,Expected children", lines[0]);
        Assert.AreEqual("2026-10-05,Monday,14:00,Yes,,1,6,0.00,0.00,,0,1,1,14", lines.Single(x => x.StartsWith("2026-10-05,Monday,14:00")));

        foreach (string secret in new[] { "Ava", "Testchild", "Morgan", "07700", "example.test", "peanut", "blue room", "Lazer", "249", "50.00" })
        {
            Assert.IsFalse(csv.Contains(secret, StringComparison.OrdinalIgnoreCase), $"The export must not contain \"{secret}\".");
        }
    }

    [TestMethod]
    public async Task ExportCsv_KeepsTheOldColumnsWhenTheCompanyDoesNotUseParties()
    {
        Fixture f = await SeedAsync();
        await SwitchOffAsync(f, CompanyFeature.Parties);
        await AddGameAsync(f, f.IpswichKiosk, Local(Monday, 14, 30));

        Result<InsightsExportFile> result = await ServiceFor(f).ExportCsvAsync(AdminScope(f), Week);

        string csv = Encoding.UTF8.GetString(result.Data!.Content).TrimStart('\uFEFF');
        Assert.AreEqual(string.Join(',', InsightsExportBuilder.Header), csv.Split("\r\n")[0]);
    }
}
