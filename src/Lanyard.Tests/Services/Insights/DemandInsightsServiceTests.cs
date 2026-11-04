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

        Assert.AreEqual(string.Join(',', InsightsExportBuilder.Header), lines[0]);
        Assert.AreEqual(3, lines.Length);
        Assert.AreEqual("2026-10-05,Monday,14:00,Yes,,1,6,1.00,0.00,1.00", lines[1]);
        Assert.IsFalse(csv.Contains("amy", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ExportCsv_FailsWhenTheSummaryIsRefused()
    {
        Fixture f = await SeedAsync();

        Result<InsightsExportFile> result = await ServiceFor(f, new TestTenant(null, isSystem: true)).ExportCsvAsync(AdminScope(f), Week);

        Assert.IsFalse(result.IsSuccess);
    }
}
