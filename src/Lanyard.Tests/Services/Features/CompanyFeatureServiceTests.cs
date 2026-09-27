using System.Security.Claims;
using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
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
        IMemoryCache? cache = null)
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
            NullLogger<CompanyFeatureService>.Instance);
    }

    [TestMethod]
    public async Task GetFeaturesAsync_EverythingIsOnForACompanyNobodyHasConfigured()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<List<CompanyFeatureState>> result = await GetService(options).GetFeaturesAsync(company.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(System.Enum.GetValues<CompanyFeature>().Length, result.Data!.Count);
        Assert.IsTrue(result.Data.All(x => x.IsEnabled));
    }

    [TestMethod]
    public async Task SetFeatureEnabledAsync_TurnsAFeatureOffAndBackOn_AndClearsTheCache()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        CompanyFeatureService service = GetService(options);

        // Prime the cache so the save has to invalidate it.
        await service.GetFeaturesAsync(company.Id);

        Result<bool> off = await service.SetFeatureEnabledAsync(SchedulingTestHelpers.AdminScope, company.Id, CompanyFeature.Training, false, "admin");
        Assert.IsTrue(off.IsSuccess, off.Error);

        List<CompanyFeatureState> afterOff = (await service.GetFeaturesAsync(company.Id)).Data!;
        Assert.IsFalse(afterOff.Single(x => x.Info.Feature == CompanyFeature.Training).IsEnabled);
        Assert.IsTrue(afterOff.Single(x => x.Info.Feature == CompanyFeature.StaffScheduling).IsEnabled);

        Result<bool> on = await service.SetFeatureEnabledAsync(SchedulingTestHelpers.AdminScope, company.Id, CompanyFeature.Training, true, "admin");
        Assert.IsTrue(on.IsSuccess, on.Error);

        List<CompanyFeatureState> afterOn = (await service.GetFeaturesAsync(company.Id)).Data!;
        Assert.IsTrue(afterOn.All(x => x.IsEnabled));

        await using ApplicationDbContext ctx = new(options);
        CompanyFeatureSetting row = await ctx.CompanyFeatureSettings.SingleAsync();
        Assert.AreEqual("admin", row.UpdatedByUserId);
    }

    [TestMethod]
    public async Task SetFeatureEnabledAsync_RefusesAManager()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<bool> result = await GetService(options).SetFeatureEnabledAsync(
            SchedulingTestHelpers.ManagerScopeFor(location), company.Id, CompanyFeature.Training, false, "manager");

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.CompanyFeatureSettings.CountAsync());
    }

    [TestMethod]
    public async Task SetFeatureEnabledAsync_FailsForAnUnknownCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();

        Result<bool> result = await GetService(options).SetFeatureEnabledAsync(
            SchedulingTestHelpers.AdminScope, 999, CompanyFeature.Training, false, "admin");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetHiddenFeaturesForCurrentUserAsync_HidesWhatTheStaffMembersCompanyHasTurnedOff()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        await GetService(options).SetFeatureEnabledAsync(SchedulingTestHelpers.AdminScope, company.Id, CompanyFeature.Training, false, "admin");

        CompanyFeatureService staff = GetService(options, SignedIn(), Result<LocationScope>.Ok(SchedulingTestHelpers.StaffScopeFor(location)));
        IReadOnlySet<CompanyFeature> hidden = await staff.GetHiddenFeaturesForCurrentUserAsync();

        CollectionAssert.AreEquivalent(new[] { CompanyFeature.Training }, hidden.ToArray());
        Assert.IsFalse(await staff.IsEnabledForCurrentUserAsync(CompanyFeature.Training));
        Assert.IsTrue(await staff.IsEnabledForCurrentUserAsync(CompanyFeature.StaffScheduling));

        // Another company's staff are unaffected.
        CompanyFeatureService otherStaff = GetService(options, SignedIn(), Result<LocationScope>.Ok(SchedulingTestHelpers.StaffScopeFor(otherLocation)));
        Assert.IsTrue(await otherStaff.IsEnabledForCurrentUserAsync(CompanyFeature.Training));
    }

    [TestMethod]
    public async Task GetHiddenFeaturesForCurrentUserAsync_HidesNothingFromAnAdmin()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        await GetService(options).SetFeatureEnabledAsync(SchedulingTestHelpers.AdminScope, company.Id, CompanyFeature.Training, false, "admin");

        CompanyFeatureService admin = GetService(options, SignedIn("Admin"),
            Result<LocationScope>.Ok(new LocationScope(true, location.Id, company.Id, location.Name)));

        Assert.AreEqual(0, (await admin.GetHiddenFeaturesForCurrentUserAsync()).Count);
    }

    [TestMethod]
    public async Task GetHiddenFeaturesForCurrentUserAsync_HidesEverythingWhenTheCompanyCantBeWorkedOut()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();

        CompanyFeatureService staff = GetService(options, SignedIn(), Result<LocationScope>.Fail("No location is set for this session."));

        IReadOnlySet<CompanyFeature> hidden = await staff.GetHiddenFeaturesForCurrentUserAsync();

        Assert.AreEqual(System.Enum.GetValues<CompanyFeature>().Length, hidden.Count);
    }

    [TestMethod]
    public void Catalog_ListsEveryFeatureExactlyOnce()
    {
        CollectionAssert.AreEquivalent(
            System.Enum.GetValues<CompanyFeature>(),
            CompanyFeatureCatalog.All.Select(x => x.Feature).ToArray());
    }
}
