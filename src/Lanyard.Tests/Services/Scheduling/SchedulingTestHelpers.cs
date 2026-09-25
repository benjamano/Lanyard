using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Lanyard.Tests.Services.Scheduling;

// Shared seeding for the scheduling service tests. Each test class still owns its own
// GetInMemoryOptions()/GetService() pair per the repo convention; this only avoids repeating
// the company/location/user seed that every scheduling test needs.
internal static class SchedulingTestHelpers
{
    public static readonly LocationScope AdminScope = new(true, null, null, null);

    public static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    public static IDbContextFactory<ApplicationDbContext> GetFactory(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return factoryMock.Object;
    }

    public static LocationScope ManagerScopeFor(Location location) =>
        new(false, location.Id, location.CompanyId, location.Name);

    public static async Task<(Company Company, Location Location)> SeedCompanyAsync(
        DbContextOptions<ApplicationDbContext> options, string companyName = "Play2Day", string locationName = "Ipswich")
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = companyName, IsActive = true };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        Location location = new() { CompanyId = company.Id, Name = locationName, IsActive = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        return (company, location);
    }

    public static async Task<UserProfile> SeedUserAsync(
        DbContextOptions<ApplicationDbContext> options, Location location, string firstName = "Ben")
    {
        await using ApplicationDbContext ctx = new(options);

        UserProfile user = new()
        {
            Id = Guid.NewGuid().ToString(),
            UserName = $"{firstName.ToLowerInvariant()}-{Guid.NewGuid():N}",
            FirstName = firstName,
            LastName = "Tester"
        };

        ctx.Users.Add(user);
        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = user.Id, LocationId = location.Id, CreateDate = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        return user;
    }

    public static async Task<StaffPosition> SeedPositionAsync(
        DbContextOptions<ApplicationDbContext> options, Company company, string name, int sortOrder = 0)
    {
        await using ApplicationDbContext ctx = new(options);

        StaffPosition position = new() { Id = Guid.NewGuid(), CompanyId = company.Id, Name = name, SortOrder = sortOrder, IsActive = true };
        ctx.StaffPositions.Add(position);
        await ctx.SaveChangesAsync();

        return position;
    }
}
