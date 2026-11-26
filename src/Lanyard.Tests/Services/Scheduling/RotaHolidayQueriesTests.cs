using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class RotaHolidayQueriesTests
{
    private static readonly DateOnly Christmas = new(2026, 12, 25);

    private static RotaHoliday Holiday(Location location, DateOnly start, DateOnly? end = null, bool active = true, string name = "Christmas Day") => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = location.CompanyId,
        LocationId = location.Id,
        Name = name,
        Kind = RotaHolidayKind.Closed,
        StartDate = start,
        EndDate = end ?? start,
        IsActive = active
    };

    private static async Task<Location> AddLocationAsync(DbContextOptions<ApplicationDbContext> options, Company company, string name)
    {
        await using ApplicationDbContext ctx = new(options);
        Location location = new() { CompanyId = company.Id, Name = name, IsActive = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();
        return location;
    }

    [TestMethod]
    public async Task ForLocationsAsync_ActiveHolidaysInRange_ByLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech = await AddLocationAsync(options, company, "Wisbech");
        await TestFeatures.SwitchOnEverywhereAsync(options);

        await using (ApplicationDbContext seed = new(options))
        {
            seed.RotaHolidays.AddRange(
                Holiday(ipswich, Christmas),
                Holiday(ipswich, Christmas.AddDays(1), active: false, name: "Removed"),
                Holiday(ipswich, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 24), name: "Ends the day before"),
                Holiday(wisbech, Christmas, name: "Wisbech Christmas"));
            await seed.SaveChangesAsync();
        }

        await using ApplicationDbContext ctx = new(options);
        Dictionary<int, List<RotaHoliday>> found = await RotaHolidayQueries.ForLocationsAsync(ctx, [ipswich.Id], Christmas, Christmas.AddDays(1));

        Assert.AreEqual(1, found.Count, "only the location asked for");
        Assert.AreEqual("Christmas Day", found.For(ipswich.Id).Single().Name);
        Assert.AreEqual(0, found.For(wisbech.Id).Count);
        Assert.AreEqual(0, found.For(null).Count);
    }

    [TestMethod]
    public async Task ForLocationsAsync_IgnoresLocationsWithoutStaffScheduling()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        await using (ApplicationDbContext seed = new(options))
        {
            seed.RotaHolidays.Add(Holiday(ipswich, Christmas));
            await TestFeatures.SwitchOffForCompanyAsync(seed, company.Id, CompanyFeature.StaffScheduling);
            await seed.SaveChangesAsync();
        }

        await using ApplicationDbContext ctx = new(options);
        Dictionary<int, List<RotaHoliday>> found = await RotaHolidayQueries.ForLocationAsync(ctx, ipswich.Id, Christmas, Christmas);

        Assert.AreEqual(0, found.For(ipswich.Id).Count, "nobody can see or edit them there, so they don't shut the venue");
    }
}
