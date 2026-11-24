using System.Security.Claims;
using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Lanyard.Tests.TestUtilities;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Features;

[TestClass]
public class CompanyFeatureServiceTests
{
    private sealed class FixedAuthStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private static ClaimsPrincipal SignedIn(params string[] roles)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, "user-1")];
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static CompanyFeatureService GetService(
        DbContextOptions<ApplicationDbContext> options,
        ClaimsPrincipal? user = null,
        Result<LocationScope>? scope = null,
        IMemoryCache? cache = null,
        ITenantContext? tenant = null)
    {
        Mock<ICurrentLocationContext> locationContext = new();
        locationContext.Setup(x => x.GetScopeAsync())
            .ReturnsAsync(scope ?? Result<LocationScope>.Fail("No location is set for this session."));

        return new CompanyFeatureService(
            SchedulingTestHelpers.GetFactory(options),
            locationContext.Object,
            new FixedAuthStateProvider(user ?? new ClaimsPrincipal(new ClaimsIdentity())),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            NullLogger<CompanyFeatureService>.Instance,
            tenant);
    }

    // A company with two locations and no switches at all, unlike SeedCompanyAsync, which
    // switches everything on.
    private static async Task<(Company Company, Location Ipswich, Location Wisbech)> SeedBareCompanyAsync(DbContextOptions<ApplicationDbContext> options, string name = "Play2Day")
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = name, IsActive = true };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        Location ipswich = new() { CompanyId = company.Id, Name = $"{name} Ipswich", IsActive = true };
        Location wisbech = new() { CompanyId = company.Id, Name = $"{name} Wisbech", IsActive = true };
        ctx.Locations.AddRange(ipswich, wisbech);
        await ctx.SaveChangesAsync();

        return (company, ipswich, wisbech);
    }

    private static Task<Result<bool>> SwitchAsync(CompanyFeatureService service, Location location, CompanyFeature feature, bool on) =>
        service.SetFeatureEnabledAsync(SchedulingTestHelpers.AdminScope, location.Id, feature, on, "admin");

    [TestMethod]
    public async Task GetFeatureGridAsync_EverythingIsOffAtALocationNobodyHasConfigured()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich, Location wisbech) = await SeedBareCompanyAsync(options);

        Result<CompanyFeatureGrid> result = await GetService(options).GetFeatureGridAsync(company.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(System.Enum.GetValues<CompanyFeature>().Length, result.Data!.Features.Count);
        CollectionAssert.AreEquivalent(new[] { ipswich.Id, wisbech.Id }, result.Data.Locations.Select(x => x.LocationId).ToArray());
        Assert.AreEqual(0, result.Data.Enabled.Count);

        List<CompanyFeatureState> anywhere = (await GetService(options).GetFeaturesAsync(company.Id)).Data!;
        Assert.IsTrue(anywhere.All(x => !x.IsEnabled));
    }

    [TestMethod]
    public async Task GetFeatureGridAsync_LeavesOutDeactivatedLocations()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich, Location wisbech) = await SeedBareCompanyAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            (await ctx.Locations.SingleAsync(x => x.Id == wisbech.Id)).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        CompanyFeatureGrid grid = (await GetService(options).GetFeatureGridAsync(company.Id)).Data!;

        CollectionAssert.AreEqual(new[] { ipswich.Id }, grid.Locations.Select(x => x.LocationId).ToArray());
    }

    [TestMethod]
    public async Task SetFeatureEnabledAsync_SwitchesOneLocationOnly_AndClearsTheCache()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich, Location wisbech) = await SeedBareCompanyAsync(options);
        CompanyFeatureService service = GetService(options);

        // Prime the cache so the save has to invalidate it.
        await service.GetFeatureGridAsync(company.Id);

        Result<bool> on = await SwitchAsync(service, ipswich, CompanyFeature.Training, true);
        Assert.IsTrue(on.IsSuccess, on.Error);

        CompanyFeatureGrid grid = (await service.GetFeatureGridAsync(company.Id)).Data!;
        Assert.IsTrue(grid.IsEnabled(ipswich.Id, CompanyFeature.Training));
        Assert.IsFalse(grid.IsEnabled(wisbech.Id, CompanyFeature.Training));
        Assert.IsFalse(grid.IsEnabled(ipswich.Id, CompanyFeature.Chat));
        Assert.IsTrue(await service.IsEnabledForLocationAsync(ipswich.Id, CompanyFeature.Training));
        Assert.IsFalse(await service.IsEnabledForLocationAsync(wisbech.Id, CompanyFeature.Training));

        Result<bool> off = await SwitchAsync(service, ipswich, CompanyFeature.Training, false);
        Assert.IsTrue(off.IsSuccess, off.Error);
        Assert.IsFalse(await service.IsEnabledForLocationAsync(ipswich.Id, CompanyFeature.Training));

        await using ApplicationDbContext ctx = new(options);
        LocationFeatureSetting row = await ctx.LocationFeatureSettings.SingleAsync();
        Assert.AreEqual(ipswich.Id, row.LocationId);
        Assert.IsFalse(row.IsEnabled);
        Assert.AreEqual("admin", row.UpdatedByUserId);
    }

    [TestMethod]
    public async Task SetFeatureEnabledAsync_RefusesAManager()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich, _) = await SeedBareCompanyAsync(options);

        Result<bool> result = await GetService(options).SetFeatureEnabledAsync(
            SchedulingTestHelpers.ManagerScopeFor(ipswich), ipswich.Id, CompanyFeature.Training, true, "manager");

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.LocationFeatureSettings.CountAsync());
    }

    [TestMethod]
    public async Task SetFeatureEnabledAsync_RefusesACompanysOwnAdmin()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich, _) = await SeedBareCompanyAsync(options);

        Mock<ITenantContext> tenant = new();
        tenant.SetupGet(x => x.IsSystem).Returns(false);
        tenant.SetupGet(x => x.IsPlatformAdmin).Returns(false);

        Result<bool> result = await GetService(options, tenant: tenant.Object).SetFeatureEnabledAsync(
            SchedulingTestHelpers.AdminScope, ipswich.Id, CompanyFeature.Training, true, "company-admin");

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.LocationFeatureSettings.CountAsync());
    }

    [TestMethod]
    public async Task SetFeatureEnabledAsync_FailsForAnUnknownOrDeactivatedLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, _, Location wisbech) = await SeedBareCompanyAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            (await ctx.Locations.SingleAsync(x => x.Id == wisbech.Id)).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        CompanyFeatureService service = GetService(options);

        Assert.IsFalse((await service.SetFeatureEnabledAsync(SchedulingTestHelpers.AdminScope, 999, CompanyFeature.Training, true, "admin")).IsSuccess);
        Assert.IsFalse((await SwitchAsync(service, wisbech, CompanyFeature.Training, true)).IsSuccess);
    }

    [TestMethod]
    public async Task IsEnabledForCompanyAsync_IsOnWhenAnyActiveLocationHasIt_EvenForAnAdmin()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich, Location wisbech) = await SeedBareCompanyAsync(options);
        (Company other, _, _) = await SeedBareCompanyAsync(options, "Other Co");

        CompanyFeatureService admin = GetService(options, SignedIn("Admin"));
        await SwitchAsync(admin, wisbech, CompanyFeature.AudioClips, true);

        Assert.IsTrue(await admin.IsEnabledForCompanyAsync(company.Id, CompanyFeature.AudioClips));
        Assert.IsFalse(await admin.IsEnabledForCompanyAsync(company.Id, CompanyFeature.Training));
        Assert.IsFalse(await admin.IsEnabledForCompanyAsync(other.Id, CompanyFeature.AudioClips));

        // The per-user check still lets an Admin see everything.
        Assert.IsTrue(await admin.IsEnabledForCurrentUserAsync(CompanyFeature.Training));

        // Deactivating the only location that has it takes it away from the company.
        await using (ApplicationDbContext ctx = new(options))
        {
            (await ctx.Locations.SingleAsync(x => x.Id == wisbech.Id)).IsActive = false;
            await ctx.SaveChangesAsync();
        }

        CompanyFeatureService fresh = GetService(options);
        Assert.IsFalse(await fresh.IsEnabledForCompanyAsync(company.Id, CompanyFeature.AudioClips));

        await using ApplicationDbContext check = new(options);
        Assert.IsFalse(await CompanyFeatureQueries.IsEnabledForCompanyAsync(check, company.Id, CompanyFeature.AudioClips));
        Assert.IsTrue(await CompanyFeatureQueries.IsEnabledForLocationAsync(check, wisbech.Id, CompanyFeature.AudioClips));
        Assert.IsFalse(await CompanyFeatureQueries.IsEnabledForLocationAsync(check, ipswich.Id, CompanyFeature.AudioClips));
    }

    [TestMethod]
    public async Task GetHiddenFeaturesForCurrentUserAsync_FollowsTheStaffMembersOwnLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich, Location wisbech) = await SeedBareCompanyAsync(options);
        await TestFeatures.SwitchOnEverywhereAsync(options);

        await SwitchAsync(GetService(options), wisbech, CompanyFeature.Training, false);

        CompanyFeatureService ipswichStaff = GetService(options, SignedIn(), Result<LocationScope>.Ok(SchedulingTestHelpers.StaffScopeFor(ipswich)));
        Assert.AreEqual(0, (await ipswichStaff.GetHiddenFeaturesForCurrentUserAsync()).Count);

        CompanyFeatureService wisbechStaff = GetService(options, SignedIn(), Result<LocationScope>.Ok(SchedulingTestHelpers.StaffScopeFor(wisbech)));
        IReadOnlySet<CompanyFeature> hidden = await wisbechStaff.GetHiddenFeaturesForCurrentUserAsync();

        CollectionAssert.AreEquivalent(new[] { CompanyFeature.Training }, hidden.ToArray());
        Assert.IsFalse(await wisbechStaff.IsEnabledForCurrentUserAsync(CompanyFeature.Training));
        Assert.IsTrue(await wisbechStaff.IsEnabledForCurrentUserAsync(CompanyFeature.StaffScheduling));
    }

    [TestMethod]
    public async Task GetHiddenFeaturesForCurrentUserAsync_HidesEverythingAtANewLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location ipswich, _) = await SeedBareCompanyAsync(options);

        CompanyFeatureService staff = GetService(options, SignedIn(), Result<LocationScope>.Ok(SchedulingTestHelpers.StaffScopeFor(ipswich)));

        Assert.AreEqual(System.Enum.GetValues<CompanyFeature>().Length, (await staff.GetHiddenFeaturesForCurrentUserAsync()).Count);
    }

    [TestMethod]
    public async Task GetHiddenFeaturesForCurrentUserAsync_HidesNothingFromAnAdmin()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich, _) = await SeedBareCompanyAsync(options);

        CompanyFeatureService admin = GetService(options, SignedIn("Admin"),
            Result<LocationScope>.Ok(new LocationScope(true, ipswich.Id, company.Id, ipswich.Name)));

        Assert.AreEqual(0, (await admin.GetHiddenFeaturesForCurrentUserAsync()).Count);
    }

    [TestMethod]
    public async Task GetHiddenFeaturesForCurrentUserAsync_HidesEverythingWhenTheLocationCantBeWorkedOut()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();

        CompanyFeatureService staff = GetService(options, SignedIn(), Result<LocationScope>.Fail("No location is set for this session."));

        IReadOnlySet<CompanyFeature> hidden = await staff.GetHiddenFeaturesForCurrentUserAsync();

        Assert.AreEqual(System.Enum.GetValues<CompanyFeature>().Length, hidden.Count);
    }

    [TestMethod]
    public async Task AddingALocation_ShowsItInTheGridStraightAway()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _, _) = await SeedBareCompanyAsync(options);
        MemoryCache cache = new(new MemoryCacheOptions());
        CompanyFeatureService service = GetService(options, cache: cache);

        Assert.AreEqual(2, (await service.GetFeatureGridAsync(company.Id)).Data!.Locations.Count);

        CompanyLocationService locations = new(SchedulingTestHelpers.GetFactory(options), cache: cache);
        Result<Location> added = await locations.SaveLocationAsync(new Location { CompanyId = company.Id, Name = "Norwich" });
        Assert.IsTrue(added.IsSuccess, added.Error);

        CompanyFeatureGrid grid = (await service.GetFeatureGridAsync(company.Id)).Data!;
        Assert.AreEqual(3, grid.Locations.Count);
        Assert.IsFalse(grid.Enabled.Any(x => x.LocationId == added.Data!.Id));
    }

    [TestMethod]
    public void Catalog_ListsEveryFeatureExactlyOnce()
    {
        CollectionAssert.AreEquivalent(
            System.Enum.GetValues<CompanyFeature>(),
            CompanyFeatureCatalog.All.Select(x => x.Feature).ToArray());
    }
}
