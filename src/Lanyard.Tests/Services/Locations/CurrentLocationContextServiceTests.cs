using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Security.Claims;

namespace Lanyard.Tests.Services.Locations;

[TestClass]
public class CurrentLocationContextServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static Mock<AuthenticationStateProvider> BuildAuthStateProvider(ClaimsPrincipal principal)
    {
        Mock<AuthenticationStateProvider> mock = new();
        mock.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(principal));
        return mock;
    }

    private static ClaimsPrincipal BuildPrincipal(bool isAdmin, int? locationId)
    {
        List<Claim> claims = [new Claim(ClaimTypes.Name, "test-user")];

        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }

        if (locationId.HasValue)
        {
            claims.Add(new Claim(LocationClaimTypes.LocationId, locationId.Value.ToString()));
        }

        ClaimsIdentity identity = new(claims, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    [TestMethod]
    public async Task CurrentLocationContext_AdminUser_BypassesLocationRequirement()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<AuthenticationStateProvider> authProvider = BuildAuthStateProvider(BuildPrincipal(isAdmin: true, locationId: null));
        CurrentLocationContextService service = new(authProvider.Object, factoryMock.Object);

        Result<LocationScope> result = await service.GetScopeAsync();

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue(result.Data!.IsAdmin);
        Assert.IsNull(result.Data.LocationId);
    }

    [TestMethod]
    public async Task CurrentLocationContext_NonAdminWithValidClaim_ResolvesLocationScope()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        int locationId;
        await using (ApplicationDbContext ctx = new(options))
        {
            Company company = new() { Name = "Play2Day", IsActive = true };
            ctx.Companies.Add(company);
            await ctx.SaveChangesAsync();

            Location location = new() { CompanyId = company.Id, Name = "Ipswich", IsActive = true };
            ctx.Locations.Add(location);
            await ctx.SaveChangesAsync();
            locationId = location.Id;
        }

        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<AuthenticationStateProvider> authProvider = BuildAuthStateProvider(BuildPrincipal(isAdmin: false, locationId: locationId));
        CurrentLocationContextService service = new(authProvider.Object, factoryMock.Object);

        Result<LocationScope> result = await service.GetScopeAsync();

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Data!.IsAdmin);
        Assert.AreEqual(locationId, result.Data.LocationId);
        Assert.AreEqual("Play2Day Ipswich", result.Data.LocationDisplayName);
    }

    [TestMethod]
    public async Task CurrentLocationContext_NonAdminWithMissingClaim_Fails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<AuthenticationStateProvider> authProvider = BuildAuthStateProvider(BuildPrincipal(isAdmin: false, locationId: null));
        CurrentLocationContextService service = new(authProvider.Object, factoryMock.Object);

        Result<LocationScope> result = await service.GetScopeAsync();

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CurrentLocationContext_NonAdminWithInactiveLocation_Fails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        int locationId;
        await using (ApplicationDbContext ctx = new(options))
        {
            Company company = new() { Name = "Play2Day", IsActive = true };
            ctx.Companies.Add(company);
            await ctx.SaveChangesAsync();

            Location location = new() { CompanyId = company.Id, Name = "Closed", IsActive = false };
            ctx.Locations.Add(location);
            await ctx.SaveChangesAsync();
            locationId = location.Id;
        }

        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<AuthenticationStateProvider> authProvider = BuildAuthStateProvider(BuildPrincipal(isAdmin: false, locationId: locationId));
        CurrentLocationContextService service = new(authProvider.Object, factoryMock.Object);

        Result<LocationScope> result = await service.GetScopeAsync();

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Your selected location is no longer available. Please log in again.", result.Error);
    }
}
