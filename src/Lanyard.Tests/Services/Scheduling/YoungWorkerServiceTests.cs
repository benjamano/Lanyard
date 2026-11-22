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

    private static readonly DateOnly Sixteen = new(2010, 3, 3);

    [TestMethod]
    public async Task SaveAsync_ManagerSetsDateOfBirth_DatesWorkedOutAndWhoSetIt()
    {
        World w = await SeedAsync();

        Result<YoungWorkerStatusView?> saved = await GetService(w.Options).SaveAsync(w.ManagerScope, w.Amy.Id, Sixteen, w.Sam.Id);

        Assert.IsTrue(saved.IsSuccess, saved.Error);
        Assert.AreEqual(Sixteen, saved.Data!.DateOfBirth);
        Assert.AreEqual(new DateOnly(2028, 3, 3), saved.Data.EighteenthBirthday);
        Assert.AreEqual(new DateOnly(2026, 6, 26), saved.Data.SchoolLeavingDate);
        Assert.AreEqual("Sam Tester", saved.Data.UpdatedByName);

        await using ApplicationDbContext ctx = new(w.Options);
        YoungWorkerStatus row = await ctx.YoungWorkerStatuses.SingleAsync();
        Assert.AreEqual(w.Company.Id, row.CompanyId);
        Assert.AreEqual(w.Sam.Id, row.UpdatedByUserId);
        Assert.AreEqual(NowUtc, row.UpdateDate);
    }

    [TestMethod]
    public async Task SaveAsync_Null_DeletesTheRow()
    {
        World w = await SeedAsync();
        YoungWorkerService service = GetService(w.Options);
        await service.SaveAsync(w.ManagerScope, w.Amy.Id, Sixteen, w.Sam.Id);

        Result<YoungWorkerStatusView?> cleared = await service.SaveAsync(w.ManagerScope, w.Amy.Id, null, w.Sam.Id);

        Assert.IsTrue(cleared.IsSuccess, cleared.Error);
        Assert.IsNull(cleared.Data);

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.IsFalse(await ctx.YoungWorkerStatuses.AnyAsync());
    }

    [TestMethod]
    public async Task SaveAsync_StaffMember_Refused()
    {
        World w = await SeedAsync();

        Result<YoungWorkerStatusView?> saved = await GetService(w.Options).SaveAsync(w.StaffScope, w.Amy.Id, Sixteen, w.Sam.Id);

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

        Result<YoungWorkerStatusView?> saved = await GetService(w.Options).SaveAsync(w.ManagerScope, w.Sam.Id, null, w.Sam.Id);

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
            .SaveAsync(SchedulingTestHelpers.ManagerScopeFor(elsewhere), w.Amy.Id, Sixteen, otherManager.Id);

        Assert.IsFalse(saved.IsSuccess);
        StringAssert.Contains(saved.Error, "people you manage");
    }

    [TestMethod]
    public async Task SaveAsync_ManagerChangingAnAdmin_Refused_AdminAllowed()
    {
        World w = await SeedAsync();
        await MakeAdminAsync(w.Options, w.Amy);

        Result<YoungWorkerStatusView?> byManager = await GetService(w.Options).SaveAsync(w.ManagerScope, w.Amy.Id, null, w.Sam.Id);

        Assert.IsFalse(byManager.IsSuccess);
        StringAssert.Contains(byManager.Error, "Only an Admin");

        LocationScope adminScope = new(true, w.Ipswich.Id, w.Company.Id, w.Ipswich.Name);
        Result<YoungWorkerStatusView?> byAdmin = await GetService(w.Options).SaveAsync(adminScope, w.Amy.Id, Sixteen, w.Sam.Id);

        Assert.IsTrue(byAdmin.IsSuccess, byAdmin.Error);
    }

    [TestMethod]
    public void Validate_OnlyUnder18sOldEnoughToWork()
    {
        Assert.IsNull(YoungWorkerService.Validate(new DateOnly(2008, 11, 20), Today), "turns 18 tomorrow");
        Assert.IsNull(YoungWorkerService.Validate(new DateOnly(2013, 11, 19), Today), "13 today");

        StringAssert.Contains(YoungWorkerService.Validate(new DateOnly(2008, 11, 19), Today), "18 or over");
        StringAssert.Contains(YoungWorkerService.Validate(new DateOnly(2013, 11, 20), Today), "too young");
    }

    [TestMethod]
    public async Task SaveAsync_SomeoneAlready18_Refused()
    {
        World w = await SeedAsync();

        Result<YoungWorkerStatusView?> saved = await GetService(w.Options).SaveAsync(w.ManagerScope, w.Amy.Id, new DateOnly(2000, 1, 1), w.Sam.Id);

        Assert.IsFalse(saved.IsSuccess);
        StringAssert.Contains(saved.Error, "18 or over");
    }

    [TestMethod]
    public async Task GetForCompanyAsync_OnlyPeopleStillUnder18()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Amy.Id, DateOfBirth = new DateOnly(2008, 11, 20) });
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Sam.Id, DateOfBirth = new DateOnly(2008, 11, 19) });
            await ctx.SaveChangesAsync();
        }

        Result<Dictionary<string, YoungWorkerStatus>> result = await GetService(w.Options).GetForCompanyAsync(w.ManagerScope, w.Company.Id, Today);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEquivalent(new[] { w.Amy.Id }, result.Data!.Keys.ToList());

        Result<Dictionary<string, YoungWorkerStatus>> asStaff = await GetService(w.Options).GetForCompanyAsync(w.StaffScope, w.Company.Id, Today);
        Assert.IsFalse(asStaff.IsSuccess);
    }

    [TestMethod]
    public async Task GetForCompanyAsync_AdminLookingAtAnotherCompany_GetsThatCompanysStaff()
    {
        World w = await SeedAsync();
        (Company other, Location elsewhere) = await SchedulingTestHelpers.SeedCompanyAsync(w.Options, "Other Co", "Elsewhere");
        UserProfile olly = await SchedulingTestHelpers.SeedUserAsync(w.Options, elsewhere, "Olly");

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Amy.Id, DateOfBirth = Sixteen });
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = other.Id, UserId = olly.Id, DateOfBirth = Sixteen });
            await ctx.SaveChangesAsync();
        }

        // Signed in at the first company, looking at the other one.
        LocationScope admin = new(true, w.Ipswich.Id, w.Company.Id, w.Ipswich.Name);
        Result<Dictionary<string, YoungWorkerStatus>> result = await GetService(w.Options).GetForCompanyAsync(admin, other.Id, Today);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEquivalent(new[] { olly.Id }, result.Data!.Keys.ToList());

        Result<Dictionary<string, YoungWorkerStatus>> managerElsewhere = await GetService(w.Options).GetForCompanyAsync(w.ManagerScope, other.Id, Today);
        Assert.IsFalse(managerElsewhere.IsSuccess, "a Manager only sees their own company");
    }

    [TestMethod]
    public async Task IsYoungWorkerAsync_TrueOnlyWhileUnder18()
    {
        World w = await SeedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Amy.Id, DateOfBirth = new DateOnly(2011, 12, 25) });
            await ctx.SaveChangesAsync();
        }

        Assert.IsTrue((await GetService(w.Options).IsYoungWorkerAsync(w.Amy.Id, w.Company.Id, Today)).Data);
        Assert.IsFalse((await GetService(w.Options).IsYoungWorkerAsync(w.Amy.Id, w.Company.Id, new DateOnly(2029, 12, 25))).Data);
        Assert.IsFalse((await GetService(w.Options).IsYoungWorkerAsync(w.Sam.Id, w.Company.Id, Today)).Data);
    }

    [TestMethod]
    public async Task RemoveExpiredAsync_DeletesOnlyPeopleWhoHaveTurned18()
    {
        World w = await SeedAsync();
        Guid turns18Tomorrow = Guid.NewGuid();
        Guid atSchool = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Amy.Id, DateOfBirth = new DateOnly(2008, 11, 19) });
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = Guid.NewGuid(), CompanyId = w.Company.Id, UserId = w.Sam.Id, DateOfBirth = new DateOnly(2005, 1, 1) });
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = turns18Tomorrow, CompanyId = 99, UserId = w.Amy.Id, DateOfBirth = new DateOnly(2008, 11, 20) });
            ctx.YoungWorkerStatuses.Add(new YoungWorkerStatus { Id = atSchool, CompanyId = 98, UserId = w.Sam.Id, DateOfBirth = new DateOnly(2011, 12, 25) });
            await ctx.SaveChangesAsync();
        }

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            Assert.AreEqual(2, await YoungWorkerService.RemoveExpiredAsync(ctx, Today));
        }

        await using ApplicationDbContext verify = new(w.Options);
        CollectionAssert.AreEquivalent(new[] { turns18Tomorrow, atSchool },
            await verify.YoungWorkerStatuses.IgnoreQueryFilters().Select(x => x.Id).ToListAsync());
    }

    // --- Dates worked out from a date of birth ---

    [TestMethod]
    public void IsUnder18On_UntilTheDayBeforeTheir18thBirthday()
    {
        DateOnly born = new(2008, 11, 20);

        Assert.IsTrue(YoungWorkerAge.IsUnder18On(born, new DateOnly(2026, 11, 19)));
        Assert.IsFalse(YoungWorkerAge.IsUnder18On(born, new DateOnly(2026, 11, 20)));
    }

    [TestMethod]
    public void EighteenthBirthday_LeapDayBirthday_IsFirstOfMarch()
    {
        DateOnly leap = new(2008, 2, 29);

        Assert.AreEqual(new DateOnly(2026, 3, 1), YoungWorkerAge.EighteenthBirthday(leap));
        Assert.IsTrue(YoungWorkerAge.IsUnder18On(leap, new DateOnly(2026, 2, 28)));
        Assert.IsFalse(YoungWorkerAge.IsUnder18On(leap, new DateOnly(2026, 3, 1)));
    }

    [TestMethod]
    public void SchoolLeavingDate_LastFridayInJuneOfTheSchoolYearTheyTurn16()
    {
        // The youngest and oldest in one school year leave on the same day.
        Assert.AreEqual(new DateOnly(2026, 6, 26), YoungWorkerAge.SchoolLeavingDate(new DateOnly(2010, 8, 31)));
        Assert.AreEqual(new DateOnly(2027, 6, 25), YoungWorkerAge.SchoolLeavingDate(new DateOnly(2010, 9, 1)));
        Assert.AreEqual(new DateOnly(2027, 6, 25), YoungWorkerAge.SchoolLeavingDate(new DateOnly(2011, 1, 15)));
        Assert.AreEqual(new DateOnly(2028, 6, 30), YoungWorkerAge.SchoolLeavingDate(new DateOnly(2011, 12, 25)), "30 June itself when it's a Friday");
    }
}
