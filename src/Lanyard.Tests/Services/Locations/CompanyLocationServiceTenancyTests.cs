using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Locations;

// Companies and locations aren't tenant-filtered rows (login lists them all), so
// CompanyLocationService is what stops a company's Admin - the demo company's included - from
// seeing or changing another company.
[TestClass]
public class CompanyLocationServiceTenancyTests
{
    private sealed class TestTenant(int? companyId, bool isPlatformAdmin = false) : ITenantContext
    {
        public bool IsSystem => false;
        public int? CompanyId => companyId;
        public bool IsPlatformAdmin => isPlatformAdmin;
        public int? ManageableCompanyId => isPlatformAdmin ? null : companyId ?? -1;
        public bool CanManageCompany(int id) => ManageableCompanyId is null || ManageableCompanyId == id;
    }

    private record Fixture(
        DbContextOptions<ApplicationDbContext> Options,
        Company Mine,
        Location MyLocation,
        Company Theirs,
        Location TheirLocation,
        UserProfile User);

    private static async Task<Fixture> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company mine, Location myLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Demo Co", "Demo Town");
        (Company theirs, Location theirLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Play2Day", "Ipswich");

        await using ApplicationDbContext ctx = new(options);
        UserProfile user = new() { Id = Guid.NewGuid().ToString(), UserName = "someone", FirstName = "Some", LastName = "One" };
        ctx.Users.Add(user);
        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = user.Id, LocationId = theirLocation.Id });
        await ctx.SaveChangesAsync();

        return new Fixture(options, mine, myLocation, theirs, theirLocation, user);
    }

    private static CompanyLocationService ServiceFor(Fixture f, ITenantContext tenant) =>
        new(SchedulingTestHelpers.GetFactory(f.Options), tenant);

    [TestMethod]
    public async Task CompanyAdmin_OnlySeesTheirOwnCompanyAndLocations()
    {
        Fixture f = await SeedAsync();
        CompanyLocationService service = ServiceFor(f, new TestTenant(f.Mine.Id));

        Result<List<Company>> companies = await service.GetCompaniesAsync();
        Result<List<Location>> locations = await service.GetLocationsAsync();
        Result<List<Location>> theirLocations = await service.GetLocationsAsync(f.Theirs.Id);

        CollectionAssert.AreEqual(new[] { f.Mine.Id }, companies.Data!.Select(x => x.Id).ToArray());
        CollectionAssert.AreEqual(new[] { f.MyLocation.Id }, locations.Data!.Select(x => x.Id).ToArray());
        Assert.AreEqual(0, theirLocations.Data!.Count);
    }

    [TestMethod]
    public async Task CompanyAdmin_CannotCreateEditOrDeactivateOtherCompanies()
    {
        Fixture f = await SeedAsync();
        CompanyLocationService service = ServiceFor(f, new TestTenant(f.Mine.Id));

        Assert.IsFalse((await service.SaveCompanyAsync(new Company { Name = "New Co" })).IsSuccess);
        Assert.IsFalse((await service.SaveCompanyAsync(new Company { Id = f.Theirs.Id, Name = "Hijacked" })).IsSuccess);
        Assert.IsFalse((await service.DeactivateCompanyAsync(f.Theirs.Id)).IsSuccess);
        Assert.IsTrue((await service.SaveCompanyAsync(new Company { Id = f.Mine.Id, Name = "Demo Co Renamed" })).IsSuccess);
    }

    [TestMethod]
    public async Task CompanyAdmin_CannotManageAnotherCompanysLocationsOrMembers()
    {
        Fixture f = await SeedAsync();
        CompanyLocationService service = ServiceFor(f, new TestTenant(f.Mine.Id));

        Assert.IsFalse((await service.SaveLocationAsync(new Location { CompanyId = f.Theirs.Id, Name = "Sneaky" })).IsSuccess);
        Assert.IsFalse((await service.DeactivateLocationAsync(f.TheirLocation.Id)).IsSuccess);
        Assert.IsFalse((await service.RemoveUserFromLocationAsync(f.User.Id, f.TheirLocation.Id)).IsSuccess);
        Assert.IsFalse((await service.AddUserToLocationAsync(f.User.Id, f.TheirLocation.Id)).IsSuccess);
        Assert.AreEqual(0, (await service.GetUsersInLocationAsync(f.TheirLocation.Id)).Data!.Count);

        await using ApplicationDbContext ctx = new(f.Options);
        Assert.IsTrue(await ctx.Locations.AnyAsync(x => x.Id == f.TheirLocation.Id && x.IsActive));
        Assert.IsTrue(await ctx.UserLocationMemberships.AnyAsync(x => x.UserId == f.User.Id && x.LocationId == f.TheirLocation.Id));
    }

    [TestMethod]
    public async Task PlatformAdmin_CanManageEveryCompany()
    {
        Fixture f = await SeedAsync();
        CompanyLocationService service = ServiceFor(f, new TestTenant(f.Mine.Id, isPlatformAdmin: true));

        Assert.AreEqual(2, (await service.GetCompaniesAsync()).Data!.Count);
        Assert.IsTrue((await service.SaveCompanyAsync(new Company { Name = "New Co" })).IsSuccess);
        Assert.IsTrue((await service.SaveLocationAsync(new Location { CompanyId = f.Theirs.Id, Name = "Wisbech" })).IsSuccess);
    }

    [TestMethod]
    public async Task CompanyAdmin_CannotPullAnotherCompanysUserIntoTheirLocation()
    {
        Fixture f = await SeedAsync();
        CompanyLocationService service = ServiceFor(f, new TestTenant(f.Mine.Id));

        // f.User belongs to the other company's location.
        Result<bool> result = await service.AddUserToLocationAsync(f.User.Id, f.MyLocation.Id);

        Assert.IsFalse(result.IsSuccess);
        await using ApplicationDbContext ctx = new(f.Options);
        Assert.IsFalse(await ctx.UserLocationMemberships.AnyAsync(x => x.UserId == f.User.Id && x.LocationId == f.MyLocation.Id));
    }

    [TestMethod]
    public async Task SessionCompanies_IsJustTheSignedInCompany_EvenForAPlatformAdmin()
    {
        Fixture f = await SeedAsync();
        CompanyLocationService service = ServiceFor(f, new TestTenant(f.Mine.Id, isPlatformAdmin: true));

        CollectionAssert.AreEqual(new[] { f.Mine.Id }, (await service.GetSessionCompaniesAsync()).Data!.Select(x => x.Id).ToArray());
    }
}
