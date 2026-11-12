using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class RotaHolidayServiceTests
{
    private const string ManagerId = "manager-user";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static RotaHolidayService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options), NullLogger<RotaHolidayService>.Instance);

    private static RotaHoliday New(int locationId, RotaHolidayKind kind, DateOnly start, DateOnly? end = null, string name = "Half term") => new()
    {
        CompanyId = 0,
        LocationId = locationId,
        Name = name,
        Kind = kind,
        StartDate = start,
        EndDate = end ?? start
    };

    [TestMethod]
    public async Task SaveHolidayAsync_CreatesForTheLocationsCompany_AndListsIt()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        LocationScope scope = SchedulingTestHelpers.ManagerScopeFor(ipswich);
        RotaHolidayService service = GetService(options);

        Result<RotaHoliday> saved = await service.SaveHolidayAsync(scope,
            New(ipswich.Id, RotaHolidayKind.SchoolHoliday, new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 30), "  October half term "), ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual(company.Id, saved.Data!.CompanyId);
        Assert.AreEqual("October half term", saved.Data.Name);

        Result<List<RotaHoliday>> list = await service.GetHolidaysAsync(scope, ipswich.Id);
        Assert.AreEqual(1, list.Data!.Count);

        Result<List<RotaHoliday>> upcoming = await service.GetHolidaysAsync(scope, ipswich.Id, new DateOnly(2026, 11, 1));
        Assert.AreEqual(0, upcoming.Data!.Count, "ended before the 1st");
    }

    [TestMethod]
    public async Task SaveHolidayAsync_StaffLikeOnlyKeptForBankHolidays()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        RotaHoliday closed = New(ipswich.Id, RotaHolidayKind.Closed, new DateOnly(2026, 12, 25), name: "Christmas Day");
        closed.StaffLike = DayOfWeek.Saturday;

        Result<RotaHoliday> saved = await GetService(options).SaveHolidayAsync(SchedulingTestHelpers.ManagerScopeFor(ipswich), closed, ManagerId);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.IsNull(saved.Data!.StaffLike);
    }

    [TestMethod]
    public async Task SaveHolidayAsync_ManagerOfAnotherLocation_Refused()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Location wisbech;
        await using (ApplicationDbContext ctx = new(options))
        {
            wisbech = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(wisbech);
            await ctx.SaveChangesAsync();
        }

        Result<RotaHoliday> saved = await GetService(options).SaveHolidayAsync(SchedulingTestHelpers.ManagerScopeFor(wisbech),
            New(ipswich.Id, RotaHolidayKind.Closed, new DateOnly(2026, 12, 25)), ManagerId);

        Assert.IsFalse(saved.IsSuccess);

        Result<List<RotaHoliday>> list = await GetService(options).GetHolidaysAsync(SchedulingTestHelpers.ManagerScopeFor(wisbech), ipswich.Id);
        Assert.IsFalse(list.IsSuccess);
    }

    [TestMethod]
    public async Task SaveHolidayAsync_Invalid_Refused()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<RotaHoliday> saved = await GetService(options).SaveHolidayAsync(SchedulingTestHelpers.ManagerScopeFor(ipswich),
            New(ipswich.Id, RotaHolidayKind.SchoolHoliday, new DateOnly(2026, 10, 30), new DateOnly(2026, 10, 26)), ManagerId);

        Assert.IsFalse(saved.IsSuccess);
    }

    [TestMethod]
    public async Task RemoveHolidayAsync_SoftDeletes()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        LocationScope scope = SchedulingTestHelpers.ManagerScopeFor(ipswich);
        RotaHolidayService service = GetService(options);

        RotaHoliday saved = (await service.SaveHolidayAsync(scope, New(ipswich.Id, RotaHolidayKind.Closed, new DateOnly(2026, 12, 25)), ManagerId)).Data!;

        Result<bool> removed = await service.RemoveHolidayAsync(scope, saved.Id, ManagerId);

        Assert.IsTrue(removed.IsSuccess, removed.Error);
        Assert.AreEqual(0, (await service.GetHolidaysAsync(scope, ipswich.Id)).Data!.Count);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.RotaHolidays.CountAsync(), "kept, just switched off");
    }

    [TestMethod]
    public async Task AddBankHolidaysAsync_OnlyInRange_SkipsDaysAlreadyMarked_AndRunningAgainAddsNothing()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        LocationScope scope = SchedulingTestHelpers.ManagerScopeFor(ipswich);
        RotaHolidayService service = GetService(options);

        // Christmas Day already marked closed.
        await service.SaveHolidayAsync(scope, New(ipswich.Id, RotaHolidayKind.Closed, new DateOnly(2026, 12, 25), name: "Christmas Day"), ManagerId);

        DateOnly from = new(2026, 11, 12);
        DateOnly to = new(2027, 12, 31);

        Result<List<RotaHoliday>> missing = await service.GetMissingBankHolidaysAsync(scope, ipswich.Id, from, to);
        Assert.IsTrue(missing.IsSuccess, missing.Error);
        Assert.AreEqual(1 + 8, missing.Data!.Count, "28 Dec 2026, then all eight of 2027");
        Assert.IsTrue(missing.Data.All(x => x.Kind == RotaHolidayKind.BankHoliday && x.StaffedLike == DayOfWeek.Sunday));

        Result<int> added = await service.AddBankHolidaysAsync(scope, ipswich.Id, from, to, ManagerId);
        Assert.AreEqual(9, added.Data);

        Result<int> again = await service.AddBankHolidaysAsync(scope, ipswich.Id, from, to, ManagerId);
        Assert.AreEqual(0, again.Data);
    }

    [TestMethod]
    public async Task AutoBuild_ClosedDay_MakesNothing_AndWarnsAboutAPartyThatDay_AndSaysWhy()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        StaffPosition host = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Host");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy");

        DateOnly saturday = new(2026, 10, 10);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = amy.Id, StaffPositionId = host.Id, IsPrimary = true, CreateDate = DateTime.UtcNow });

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

            ctx.RotaHolidays.Add(new RotaHoliday
            {
                Id = Guid.NewGuid(),
                CompanyId = company.Id,
                LocationId = ipswich.Id,
                Name = "Refit",
                Kind = RotaHolidayKind.Closed,
                StartDate = saturday,
                EndDate = saturday
            });

            await ctx.SaveChangesAsync();
        }

        IDbContextFactory<ApplicationDbContext> factory = SchedulingTestHelpers.GetFactory(options);
        RotaAutoBuildService service = new(factory, new ContractRequirementService(factory), NullLogger<RotaAutoBuildService>.Instance,
            new FixedClock(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)));

        Result<AutoBuildResult> preview = await service.PreviewAsync(SchedulingTestHelpers.ManagerScopeFor(ipswich), ipswich.Id, saturday.AddDays(-5), saturday.AddDays(1));

        Assert.IsTrue(preview.IsSuccess, preview.Error);
        Assert.AreEqual(0, preview.Data!.Lines.Count);
        Assert.AreEqual(1, preview.Data.HolidayLines.Count);
        StringAssert.Contains(preview.Data.HolidayLines[0], "Refit");
        StringAssert.Contains(preview.Data.HolidayLines[0], "no shifts");
    }
}
