using System.Security.Claims;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Tenancy;

[TestClass]
public class TenantContextTests
{
    private sealed class FixedAuthStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    // What the server's provider does outside a circuit (controllers, hosted-service scopes).
    private sealed class UnsetAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            throw new InvalidOperationException("GetAuthenticationStateAsync was called before SetAuthenticationState.");
    }

    private static ClaimsPrincipal SignedIn(params Claim[] extraClaims)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, "user-1"), .. extraClaims];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static TenantContext Build(AuthenticationStateProvider? authStateProvider, HttpContext? httpContext = null, DbContextOptions<ApplicationDbContext>? options = null)
    {
        ServiceCollection services = new();

        if (authStateProvider is not null)
        {
            services.AddSingleton(authStateProvider);
        }

        return new TenantContext(
            services.BuildServiceProvider(),
            new HttpContextAccessor { HttpContext = httpContext },
            options ?? new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    [TestMethod]
    public void NobodySignedIn_IsSystem()
    {
        TenantContext tenant = Build(new FixedAuthStateProvider(new ClaimsPrincipal(new ClaimsIdentity())));

        Assert.IsTrue(tenant.IsSystem);
        Assert.IsNull(tenant.CompanyId);
        Assert.IsNull(tenant.ManageableCompanyId);
    }

    [TestMethod]
    public void OutsideACircuitWithNoHttpContext_IsSystem()
    {
        TenantContext tenant = Build(new UnsetAuthStateProvider());

        Assert.IsTrue(tenant.IsSystem);
    }

    [TestMethod]
    public void SignedInCircuitUser_UsesTheCompanyClaim()
    {
        TenantContext tenant = Build(new FixedAuthStateProvider(SignedIn(new Claim(LocationClaimTypes.CompanyId, "7"))));

        Assert.IsFalse(tenant.IsSystem);
        Assert.AreEqual(7, tenant.CompanyId);
        Assert.AreEqual(7, tenant.ManageableCompanyId);
        Assert.IsTrue(tenant.CanManageCompany(7));
        Assert.IsFalse(tenant.CanManageCompany(1));
    }

    [TestMethod]
    public void SignedInControllerRequest_FallsBackToTheHttpContextUser()
    {
        DefaultHttpContext httpContext = new() { User = SignedIn(new Claim(LocationClaimTypes.CompanyId, "3")) };

        TenantContext tenant = Build(new UnsetAuthStateProvider(), httpContext);

        Assert.IsFalse(tenant.IsSystem);
        Assert.AreEqual(3, tenant.CompanyId);
    }

    [TestMethod]
    public async Task SessionWithoutACompanyClaim_ResolvesTheCompanyFromItsLocation()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        int locationId;
        await using (ApplicationDbContext ctx = new(options))
        {
            Company company = new() { Id = 42, Name = "Other Co", IsActive = true };
            // Unique per test run: the location->company lookup is cached for the process.
            Location location = new() { Id = Random.Shared.Next(100_000, int.MaxValue), CompanyId = company.Id, Name = "Somewhere", IsActive = true };
            ctx.Companies.Add(company);
            ctx.Locations.Add(location);
            await ctx.SaveChangesAsync();
            locationId = location.Id;
        }

        TenantContext tenant = Build(
            new FixedAuthStateProvider(SignedIn(new Claim(LocationClaimTypes.LocationId, locationId.ToString()))),
            options: options);

        Assert.AreEqual(42, tenant.CompanyId);
    }

    [TestMethod]
    public void SignedInUserWithNoLocationOrCompany_IsNotSystemAndHasNoCompany()
    {
        TenantContext tenant = Build(new FixedAuthStateProvider(SignedIn()));

        Assert.IsFalse(tenant.IsSystem, "A signed-in user must never be treated as a system caller.");
        Assert.IsNull(tenant.CompanyId);
        Assert.AreEqual(-1, tenant.ManageableCompanyId);
        Assert.IsFalse(tenant.CanManageCompany(1));
    }

    [TestMethod]
    public void PlatformAdmin_IsFilteredToTheirSessionCompanyButMayManageAnyCompany()
    {
        TenantContext tenant = Build(new FixedAuthStateProvider(SignedIn(
            new Claim(LocationClaimTypes.CompanyId, "1"),
            new Claim(ClaimTypes.Role, LanyardRoles.PlatformAdmin))));

        Assert.IsFalse(tenant.IsSystem);
        Assert.AreEqual(1, tenant.CompanyId);
        Assert.IsTrue(tenant.IsPlatformAdmin);
        Assert.IsNull(tenant.ManageableCompanyId);
        Assert.IsTrue(tenant.CanManageCompany(99));
    }
}
