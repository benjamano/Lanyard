using Lanyard.Application.Services.Projection.Bindings;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Projection.Bindings;

[TestClass]
public class OpeningHoursTodayBindingSourceTests
{
    private const int CompanyId = 1;
    private const int LocationId = 10;

    private static readonly DateTime NowUtc = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DayOfWeek Today = RotaTime.ToVenueLocal(NowUtc).DayOfWeek;
    private static readonly DayOfWeek Tomorrow = (DayOfWeek)(((int)Today + 1) % 7);

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static OpeningHoursTodayBindingSource CreateSource(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<ISystemDbContextFactory> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new OpeningHoursTodayBindingSource(factoryMock.Object);
    }

    private static async Task SeedAsync(DbContextOptions<ApplicationDbContext> options, int companyId, params (DayOfWeek Day, TimeOnly Opens, TimeOnly Closes)[] windows)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.Locations.Add(new Location { Id = LocationId, CompanyId = companyId, Name = "Arena", IsActive = true });

        foreach ((DayOfWeek day, TimeOnly opens, TimeOnly closes) in windows)
        {
            ctx.LocationOpeningHours.Add(new LocationOpeningHours { LocationId = LocationId, DayOfWeek = day, OpensAt = opens, ClosesAt = closes, IsActive = true });
        }

        await ctx.SaveChangesAsync();
    }

    private static Task<Result<string?>> ResolveAsync(OpeningHoursTodayBindingSource source, int? locationId = LocationId) =>
        source.ResolveAsync(new ProjectionBindingContext(CompanyId, locationId, Guid.NewGuid(), NowUtc), CancellationToken.None);

    [TestMethod]
    public async Task ShowsTodaysWindowsInOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options, CompanyId,
            (Today, new TimeOnly(17, 0), new TimeOnly(22, 0)),
            (Today, new TimeOnly(10, 0), new TimeOnly(14, 0)),
            (Tomorrow, new TimeOnly(9, 0), new TimeOnly(18, 0)));

        Result<string?> result = await ResolveAsync(CreateSource(options));

        Assert.AreEqual("Open today 10:00 – 14:00, 17:00 – 22:00", result.Data);
    }

    [TestMethod]
    public async Task ShowsAWindowThatRunsPastMidnight()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options, CompanyId, (Today, new TimeOnly(18, 0), new TimeOnly(2, 0)));

        Result<string?> result = await ResolveAsync(CreateSource(options));

        Assert.AreEqual("Open today 18:00 – 02:00", result.Data);
    }

    [TestMethod]
    public async Task SaysClosedToday_WhenOnlyOtherDaysHaveHours()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options, CompanyId, (Tomorrow, new TimeOnly(9, 0), new TimeOnly(18, 0)));

        Result<string?> result = await ResolveAsync(CreateSource(options));

        Assert.AreEqual("Closed today", result.Data);
    }

    [TestMethod]
    public async Task HasNoData_WhenTheLocationHasNoHoursSet()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options, CompanyId);

        Result<string?> result = await ResolveAsync(CreateSource(options));

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task HasNoData_ForAKioskWithNoLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options, CompanyId, (Today, new TimeOnly(10, 0), new TimeOnly(18, 0)));

        Result<string?> result = await ResolveAsync(CreateSource(options), locationId: null);

        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task HasNoData_ForAnotherCompanysLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedAsync(options, companyId: 2, (Today, new TimeOnly(10, 0), new TimeOnly(18, 0)));

        Result<string?> result = await ResolveAsync(CreateSource(options));

        Assert.IsNull(result.Data);
    }
}
