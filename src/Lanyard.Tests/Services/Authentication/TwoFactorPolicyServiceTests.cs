using System.Security.Claims;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Demo;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Authentication;

[TestClass]
public class TwoFactorPolicyServiceTests
{
    private sealed class FixedAuthStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private static ClaimsPrincipal SignedIn(string userId, params string[] roles)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId)];
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static TwoFactorPolicyService GetService(
        DbContextOptions<ApplicationDbContext> options,
        ClaimsPrincipal user,
        Result<LocationScope> scope,
        IMemoryCache? cache = null,
        bool isDemo = false)
    {
        Mock<ICurrentLocationContext> locationContext = new();
        locationContext.Setup(x => x.GetScopeAsync()).ReturnsAsync(scope);

        Mock<IDemoGuard> demoGuard = new();
        demoGuard.Setup(x => x.IsDemoSessionAsync()).ReturnsAsync(isDemo);

        FixedAuthStateProvider authStateProvider = new(user);

        return new TwoFactorPolicyService(
            SchedulingTestHelpers.GetFactory(options),
            locationContext.Object,
            new CurrentUserAccessor(authStateProvider),
            authStateProvider,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            NullLogger<TwoFactorPolicyService>.Instance,
            demoGuard.Object);
    }

    private static async Task SetTwoFactorEnabledAsync(DbContextOptions<ApplicationDbContext> options, string userId)
    {
        await using ApplicationDbContext ctx = new(options);
        UserProfile user = await ctx.Users.SingleAsync(x => x.Id == userId);
        user.TwoFactorEnabled = true;
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task SetRequiredForCurrentCompanyAsync_ManagerTurnsItOn_RecordsWhenAndWho()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile manager = await SchedulingTestHelpers.SeedUserAsync(options, location, "Mia");
        TwoFactorPolicyService service = GetService(options, SignedIn(manager.Id, "Manager"),
            Result<LocationScope>.Ok(SchedulingTestHelpers.ManagerScopeFor(location)));

        Result<bool> result = await service.SetRequiredForCurrentCompanyAsync(true);

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext ctx = new(options);
        Company saved = await ctx.Companies.SingleAsync(x => x.Id == company.Id);
        Assert.IsNotNull(saved.TwoFactorRequiredSince);
        Assert.AreEqual(manager.Id, saved.TwoFactorRequiredByUserId);
    }

    [TestMethod]
    public async Task SetRequiredForCurrentCompanyAsync_TurningItOff_ClearsTheRequirement()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile admin = await SchedulingTestHelpers.SeedUserAsync(options, location);
        TwoFactorPolicyService service = GetService(options, SignedIn(admin.Id, "Admin"),
            Result<LocationScope>.Ok(new LocationScope(true, location.Id, company.Id, location.Name)));

        await service.SetRequiredForCurrentCompanyAsync(true);
        Result<bool> result = await service.SetRequiredForCurrentCompanyAsync(false);

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext ctx = new(options);
        Company saved = await ctx.Companies.SingleAsync(x => x.Id == company.Id);
        Assert.IsNull(saved.TwoFactorRequiredSince);
        Assert.IsNull(saved.TwoFactorRequiredByUserId);
    }

    [TestMethod]
    public async Task SetRequiredForCurrentCompanyAsync_PlainStaff_IsRefused()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile staff = await SchedulingTestHelpers.SeedUserAsync(options, location);
        TwoFactorPolicyService service = GetService(options, SignedIn(staff.Id),
            Result<LocationScope>.Ok(SchedulingTestHelpers.StaffScopeFor(location)));

        Result<bool> result = await service.SetRequiredForCurrentCompanyAsync(true);

        Assert.IsFalse(result.IsSuccess);
        await using ApplicationDbContext ctx = new(options);
        Assert.IsNull((await ctx.Companies.SingleAsync(x => x.Id == company.Id)).TwoFactorRequiredSince);
    }

    [TestMethod]
    public async Task SetRequiredForCurrentCompanyAsync_InTheDemo_IsRefused()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile manager = await SchedulingTestHelpers.SeedUserAsync(options, location);
        TwoFactorPolicyService service = GetService(options, SignedIn(manager.Id, "Manager"),
            Result<LocationScope>.Ok(SchedulingTestHelpers.ManagerScopeFor(location)), isDemo: true);

        Result<bool> result = await service.SetRequiredForCurrentCompanyAsync(true);

        Assert.IsFalse(result.IsSuccess);
        await using ApplicationDbContext ctx = new(options);
        Assert.IsNull((await ctx.Companies.SingleAsync(x => x.Id == company.Id)).TwoFactorRequiredSince);
    }

    [TestMethod]
    public async Task GetPolicyForCurrentCompanyAsync_ListsOnlyThisCompanysStaffWithTheirStatus()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile manager = await SchedulingTestHelpers.SeedUserAsync(options, location, "Mia");
        UserProfile staff = await SchedulingTestHelpers.SeedUserAsync(options, location, "Sam");
        await SchedulingTestHelpers.SeedUserAsync(options, otherLocation, "Outsider");
        await SetTwoFactorEnabledAsync(options, manager.Id);
        TwoFactorPolicyService service = GetService(options, SignedIn(manager.Id, "Manager"),
            Result<LocationScope>.Ok(SchedulingTestHelpers.ManagerScopeFor(location)));

        Result<TwoFactorPolicyDto> result = await service.GetPolicyForCurrentCompanyAsync();

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse(result.Data!.IsRequired);
        CollectionAssert.AreEquivalent(new[] { manager.Id, staff.Id }, result.Data.Users.Select(x => x.UserId).ToArray());
        Assert.AreEqual(1, result.Data.UsersWithTwoFactor);
        Assert.IsFalse(result.Data.Users.Single(x => x.UserId == staff.Id).IsEnabled);
    }

    [TestMethod]
    public async Task IsSetupRequiredForCurrentUserAsync_RequiredAndNotSetUp_IsTrue()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile manager = await SchedulingTestHelpers.SeedUserAsync(options, location, "Mia");
        UserProfile staff = await SchedulingTestHelpers.SeedUserAsync(options, location, "Sam");
        MemoryCache cache = new(new MemoryCacheOptions());
        await GetService(options, SignedIn(manager.Id, "Manager"),
            Result<LocationScope>.Ok(SchedulingTestHelpers.ManagerScopeFor(location)), cache)
            .SetRequiredForCurrentCompanyAsync(true);

        TwoFactorPolicyService staffService = GetService(options, SignedIn(staff.Id),
            Result<LocationScope>.Ok(SchedulingTestHelpers.StaffScopeFor(location)), cache);

        Assert.IsTrue(await staffService.IsSetupRequiredForCurrentUserAsync());

        await SetTwoFactorEnabledAsync(options, staff.Id);

        Assert.IsFalse(await staffService.IsSetupRequiredForCurrentUserAsync());
    }

    [TestMethod]
    public async Task IsSetupRequiredForCurrentUserAsync_NotRequired_IsFalse()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile staff = await SchedulingTestHelpers.SeedUserAsync(options, location);
        TwoFactorPolicyService service = GetService(options, SignedIn(staff.Id),
            Result<LocationScope>.Ok(SchedulingTestHelpers.StaffScopeFor(location)));

        Assert.IsFalse(await service.IsSetupRequiredForCurrentUserAsync());
    }

    [TestMethod]
    public async Task IsSetupRequiredForCurrentUserAsync_OnlyAppliesToTheCompanyThatRequiresIt()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile manager = await SchedulingTestHelpers.SeedUserAsync(options, location, "Mia");
        UserProfile outsider = await SchedulingTestHelpers.SeedUserAsync(options, otherLocation, "Olly");
        MemoryCache cache = new(new MemoryCacheOptions());
        await GetService(options, SignedIn(manager.Id, "Manager"),
            Result<LocationScope>.Ok(SchedulingTestHelpers.ManagerScopeFor(location)), cache)
            .SetRequiredForCurrentCompanyAsync(true);

        TwoFactorPolicyService outsiderService = GetService(options, SignedIn(outsider.Id),
            Result<LocationScope>.Ok(SchedulingTestHelpers.StaffScopeFor(otherLocation)), cache);

        Assert.IsFalse(await outsiderService.IsSetupRequiredForCurrentUserAsync());
    }

    [TestMethod]
    public async Task IsSetupRequiredForCurrentUserAsync_InTheDemo_IsFalse()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile staff = await SchedulingTestHelpers.SeedUserAsync(options, location);

        await using (ApplicationDbContext ctx = new(options))
        {
            Company saved = await ctx.Companies.SingleAsync(x => x.Id == company.Id);
            saved.TwoFactorRequiredSince = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        TwoFactorPolicyService service = GetService(options, SignedIn(staff.Id),
            Result<LocationScope>.Ok(SchedulingTestHelpers.StaffScopeFor(location)), isDemo: true);

        Assert.IsFalse(await service.IsSetupRequiredForCurrentUserAsync());
    }

    [TestMethod]
    public async Task IsSetupRequiredForCurrentUserAsync_SignedOut_IsFalse()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        TwoFactorPolicyService service = GetService(options, new ClaimsPrincipal(new ClaimsIdentity()),
            Result<LocationScope>.Fail("No location is set for this session."));

        Assert.IsFalse(await service.IsSetupRequiredForCurrentUserAsync());
    }
}
