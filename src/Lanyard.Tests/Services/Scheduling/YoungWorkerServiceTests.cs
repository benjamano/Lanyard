using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class YoungWorkerServiceTests
{
    // 19 Nov 2026, midday UK time.
    private static readonly DateTime NowUtc = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 19);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed record World(DbContextOptions<ApplicationDbContext> Options, Company Company, Location Ipswich, UserProfile Amy, UserProfile Sam)
    {
        public LocationScope ManagerScope => SchedulingTestHelpers.ManagerScopeFor(Ipswich);
        public LocationScope StaffScope => SchedulingTestHelpers.StaffScopeFor(Ipswich);
    }

    private static async Task<World> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy");
        UserProfile sam = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Sam");

        return new World(options, company, ipswich, amy, sam);
    }

    private static YoungWorkerService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options), new FixedTimeProvider(NowUtc), NullLogger<YoungWorkerService>.Instance);

    private static async Task MakeAdminAsync(DbContextOptions<ApplicationDbContext> options, UserProfile user)
    {
        await using ApplicationDbContext ctx = new(options);

        ApplicationRole role = new() { Id = Guid.NewGuid().ToString(), Name = "Admin", NormalizedName = "ADMIN", CreatedByUserId = user.Id };
        ctx.Roles.Add(role);
        ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task SaveAsync_ManagerSetsDates_SavedWithWhoSetThem()
    {
        World w = await SeedAsync();

        Result<YoungWorkerStatusView?> saved = await GetService(w.Options)
            .SaveAsync(w.ManagerScope, w.Amy.Id, new DateOnly(2028, 3, 1), null, w.Sam.Id);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual(new DateOnly(2028, 3, 1), saved.Data!.UnderEighteenUntil);
        Assert.AreEqual("Sam Tester", saved.Data.UpdatedByName);

        await using ApplicationDbContext ctx = new(w.Options);
        YoungWorkerStatus row = await ctx.YoungWorkerStatuses.SingleAsync();
        Assert.AreEqual(w.Company.Id, row.CompanyId);
        Assert.AreEqual(w.Sam.Id, row.UpdatedByUserId);
        Assert.AreEqual(NowUtc, row.UpdateDate);
    }

    [TestMethod]
    public async Task SaveAsync_BothDatesNull_DeletesTheRow()
    {
        World w = await SeedAsync();
        YoungWorkerService service = GetService(w.Options);
        await service.SaveAsync(w.ManagerScope, w.Amy.Id, new DateOnly(2028, 3, 1), null, w.Sam.Id);

        Result<YoungWorkerStatusView?> cleared = await service.SaveAsync(w.ManagerScope, w.Amy.Id, null, null, w.Sam.Id);

        Assert.IsTrue(cleared.IsSuccess, cleared.Error);
        Assert.IsNull(cleared.Data);

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.IsFalse(await ctx.YoungWorkerStatuses.AnyAsync());
    }

    [TestMethod]
    public async Task SaveAsync_StaffMember_Refused()
    {
        World w = await SeedAsync();

        Result<YoungWorkerStatusView?> saved = await GetService(w.Options)
            .SaveAsync(w.StaffScope, w.Amy.Id, new DateOnly(2028, 3, 1), null, w.Sam.Id);

        Assert.IsFalse(saved.IsSuccess);
        StringAssert.Contains(saved.Error, "Managers and Admins");
    }

    [TestMethod]
    public async Task GetAsync_StaffMemberReadingTheirOwn_Refused()
    {
        World w = await SeedAsync();

        Result<YoungWorkerStatusView?> read = await GetService(w.Options).GetAsync(w.StaffScope, w.Amy.Id, w.Amy.Id);

        Assert.IsFalse(read.IsSuccess);
    }

    [TestMethod]
    public async Task SaveAsync_ManagerChangingTheirOwn_Refused()
    {
        World w = await SeedAsync();

        Result<YoungWorkerStatusView?> saved = await GetService(w.Options)
            .SaveAsync(w.ManagerScope, w.Sam.Id, null, null, w.Sam.Id);

        Assert.IsFalse(saved.IsSuccess);
        StringAssert.Contains(saved.Error, "your own");
    }

    [TestMethod]
    public async Task SaveAsync_ManagerAtAnotherCompany_Refused()
    {
        World w = await SeedAsync();
        (_, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(w.Options, "Other Co", "Elsewhere");
        UserProfile otherManager = await SchedulingTestHelpers.SeedUserAsync(w.Options, elsewhere, "Olly");

        Result<YoungWorkerStatusView?> saved = await GetService(w.Options)
            .SaveAsync(SchedulingTestHelpers.ManagerScopeFor(elsewhere), w.Amy.Id, new DateOnly(2028, 3, 1), null, otherManager.Id);

        Assert.IsFalse(saved.IsSuccess);
        StringAssert.Contains(saved.Error, "people you manage");
    }

    [TestMethod]
    public async Task SaveAsync_ManagerChangingAnAdmin_Refused_AdminAllowed()
    {
        World w = await SeedAsync();
        await MakeAdminAsync(w.Options, w.Amy);

        Result<YoungWorkerStatusView?> byManager = await GetService(w.Options)
            .SaveAsync(w.ManagerScope, w.Amy.Id, null, null, w.Sam.Id);

        Assert.IsFalse(byManager.IsSuccess);
        StringAssert.Contains(byManager.Error, "Only an Admin");

        LocationScope adminScope = new(true, w.Ipswich.Id, w.Company.Id, w.Ipswich.Name);
        Result<YoungWorkerStatusView?> byAdmin = await GetService(w.Options)
            .SaveAsync(adminScope, w.Amy.Id, new DateOnly(2028, 3, 1), null, w.Sam.Id);

        Assert.IsTrue(byAdmin.IsSuccess, byAdmin.Error);
    }

    [TestMethod]
    public void Validate_RejectsImpossibleDates()
    {
        Assert.IsNull(YoungWorkerService.Validate(new DateOnly(2028, 3, 1), new DateOnly(2027, 6, 25), Today));
        Assert.IsNull(YoungWorkerService.Validate(Today.AddYears(18), null, Today));

        StringAssert.Contains(YoungWorkerService.Validate(Today.AddYears(18).AddDays(1), null, Today), "18 years");
        StringAssert.Contains(YoungWorkerService.Validate(null, Today.AddYears(19), Today), "18 years");
        StringAssert.Contains(YoungWorkerService.Validate(new DateOnly(2027, 3, 1), new DateOnly(2027, 6, 25), Today), "before their 18th birthday");
    }

    [TestMethod]
    public async Task GetForCompanyAsync_OnlyPeopleStillYoungOnTheDay()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Amy.Id, UnderEighteenUntil = Today.AddDays(1) });
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Sam.Id, UnderEighteenUntil = Today });
            await ctx.SaveChangesAsync();
        }

        Result<Dictionary<string, YoungWorkerStatus>> result = await GetService(w.Options).GetForCompanyAsync(w.ManagerScope, Today);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEquivalent(new[] { w.Amy.Id }, result.Data!.Keys.ToList());

        Result<Dictionary<string, YoungWorkerStatus>> asStaff = await GetService(w.Options).GetForCompanyAsync(w.StaffScope, Today);
        Assert.IsFalse(asStaff.IsSuccess);
    }

    [TestMethod]
    public async Task IsYoungWorkerAsync_SchoolLeavingDateAloneCounts()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Amy.Id, SchoolLeavingAgeUntil = Today.AddMonths(6) });
            await ctx.SaveChangesAsync();
        }

        Assert.IsTrue((await GetService(w.Options).IsYoungWorkerAsync(w.Amy.Id, w.Company.Id, Today)).Data);
        Assert.IsFalse((await GetService(w.Options).IsYoungWorkerAsync(w.Sam.Id, w.Company.Id, Today)).Data);
    }

    [TestMethod]
    public async Task RemoveExpiredAsync_DeletesOnlyRowsWhoseDatesHaveAllPassed()
    {
        World w = await SeedAsync();
        Guid stillUnder18 = Guid.NewGuid();
        Guid stillAtSchool = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Amy.Id, UnderEighteenUntil = Today });
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Sam.Id, UnderEighteenUntil = Today.AddDays(-30), SchoolLeavingAgeUntil = Today.AddDays(-400) });
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = stillUnder18, CompanyId = 99, UserId = w.Amy.Id, UnderEighteenUntil = Today.AddDays(1) });
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = stillAtSchool, CompanyId = 98, UserId = w.Sam.Id, SchoolLeavingAgeUntil = Today.AddDays(1) });
            await ctx.SaveChangesAsync();
        }

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            Assert.AreEqual(2, await YoungWorkerService.RemoveExpiredAsync(ctx, Today));
        }

        await using ApplicationDbContext verify = new(w.Options);
        CollectionAssert.AreEquivalent(new[] { stillUnder18, stillAtSchool },
            await verify.YoungWorkerStatuses.IgnoreQueryFilters().Select(x => x.Id).ToListAsync());
    }

    [TestMethod]
    public void AppliesOn_EitherDateStillAhead()
    {
        YoungWorkerStatus status = new() { CompanyId = 1, UserId = "u", UnderEighteenUntil = new DateOnly(2027, 5, 10) };

        Assert.IsTrue(status.AppliesOn(new DateOnly(2027, 5, 9)));
        Assert.IsFalse(status.AppliesOn(new DateOnly(2027, 5, 10)));

        status.SchoolLeavingAgeUntil = new DateOnly(2027, 6, 25);
        Assert.IsTrue(status.AppliesOn(new DateOnly(2027, 6, 1)));
    }
}
