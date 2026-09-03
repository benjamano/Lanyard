using System.Security.Claims;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Locations;

/// <summary>
/// Who may change how a venue runs its service: its ordering switch, opening hours, fulfilment
/// mode and receipt printer.
///
/// Wider than administering a company, because the CanManageKitchen role exists for people who
/// run a kitchen and nothing else. The scope has to be the company rather than the single venue,
/// because that is what the venue picker on every kitchen screen offers - VisibleLocations
/// returns the user's whole company. A narrower check here would put back the exact fault this
/// method was written to remove: a fully populated picker on which half the entries refuse to
/// save, with no way for the user to tell which.
/// </summary>
[TestClass]
public class VenueOperationsAccessTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private const string UserId = "kitchen-user";

    private static CompanyAccessService GetService(
        DbContextOptions<ApplicationDbContext> options, params string[] roles)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, UserId)];
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        ClaimsPrincipal user = new(new ClaimsIdentity(claims, "test"));

        Mock<AuthenticationStateProvider> auth = new();
        auth.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(user));

        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new CompanyAccessService(
            auth.Object, factory.Object, new Mock<ILogger<CompanyAccessService>>().Object);
    }

    /// <summary>Two venues in one company, plus a second company's venue, and the user works the first venue.</summary>
    private static async Task<(int Worked, int SisterVenue, int OtherCompanyVenue)> SeedAsync(
        DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);

        Company mine = new() { Name = "Play2Day", IsActive = true };
        Company theirs = new() { Name = "Partyman", IsActive = true };
        ctx.Companies.AddRange(mine, theirs);
        await ctx.SaveChangesAsync();

        Location worked = new() { CompanyId = mine.Id, Name = "Ipswich", IsActive = true };
        Location sister = new() { CompanyId = mine.Id, Name = "Wisbech", IsActive = true };
        Location other = new() { CompanyId = theirs.Id, Name = "Norwich", IsActive = true };
        ctx.Locations.AddRange(worked, sister, other);
        await ctx.SaveChangesAsync();

        ctx.Users.Add(new UserProfile { Id = UserId, UserName = UserId, FirstName = "Kit", LastName = "Chen" });
        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = UserId, LocationId = worked.Id });
        await ctx.SaveChangesAsync();

        return (worked.Id, sister.Id, other.Id);
    }

    [TestMethod]
    public async Task CanManageKitchen_MayRunTheVenueTheyWorkAt()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (int worked, _, _) = await SeedAsync(options);

        Assert.IsTrue(await GetService(options, "CanManageKitchen").CanManageVenueOperationsAsync(worked));
    }

    /// <summary>
    /// The venue picker offers every venue in the company, so every one of them has to save.
    /// </summary>
    [TestMethod]
    public async Task CanManageKitchen_MayRunASisterVenueInTheSameCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (_, int sister, _) = await SeedAsync(options);

        Assert.IsTrue(await GetService(options, "CanManageKitchen").CanManageVenueOperationsAsync(sister));
    }

    [TestMethod]
    public async Task CanManageKitchen_MayNotRunAnotherCompanysVenue()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (_, _, int other) = await SeedAsync(options);

        Assert.IsFalse(await GetService(options, "CanManageKitchen").CanManageVenueOperationsAsync(other));
    }

    /// <summary>Running a kitchen is not administering a company: the wider role does not follow from it.</summary>
    [TestMethod]
    public async Task CanManageKitchen_DoesNotGrantCompanyAdministration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (int worked, _, _) = await SeedAsync(options);

        CompanyAccessService service = GetService(options, "CanManageKitchen");

        Assert.IsTrue(await service.CanManageVenueOperationsAsync(worked));
        Assert.IsFalse(await service.CanAdministerLocationAsync(worked));
    }

    [TestMethod]
    public async Task AUserWithNeitherRole_MayRunNothing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (int worked, _, _) = await SeedAsync(options);

        Assert.IsFalse(await GetService(options, "Staff").CanManageVenueOperationsAsync(worked));
    }

    [TestMethod]
    public async Task AnAdmin_MayRunAnyVenue()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        (_, _, int other) = await SeedAsync(options);

        Assert.IsTrue(await GetService(options, "Admin").CanManageVenueOperationsAsync(other));
    }
}
