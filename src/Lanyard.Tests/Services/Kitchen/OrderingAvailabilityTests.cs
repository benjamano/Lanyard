using Lanyard.Application.Services.Kitchen;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Kitchen;

/// <summary>
/// Whether a venue is taking orders: the manual switch and the weekly timetable together.
///
/// Times are set relative to the venue's own clock rather than hardcoded, so these do not start
/// failing at a particular hour of the day or when the clocks change.
/// </summary>
[TestClass]
public class OrderingAvailabilityTests
{
    private const string Zone = "Europe/London";

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static OrderingAvailabilityService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new OrderingAvailabilityService(
            factoryMock.Object, new Mock<ILogger<OrderingAvailabilityService>>().Object);
    }

    private static DateTime NowAtVenue()
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);

        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
    }

    private static async Task<int> SeedVenueAsync(
        DbContextOptions<ApplicationDbContext> options,
        bool orderingEnabled = true,
        params (DayOfWeek Day, TimeOnly Opens, TimeOnly Closes)[] hours)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = "Play2Day", IsActive = true };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        Location location = new()
        {
            CompanyId = company.Id,
            Name = "Ipswich",
            IsActive = true,
            OrderingEnabled = orderingEnabled,
            TimeZoneId = Zone
        };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        foreach ((DayOfWeek day, TimeOnly opens, TimeOnly closes) in hours)
        {
            ctx.LocationOpeningHours.Add(new LocationOpeningHours
            {
                LocationId = location.Id,
                DayOfWeek = day,
                OpensAt = opens,
                ClosesAt = closes,
                CreateDate = DateTime.UtcNow
            });
        }

        await ctx.SaveChangesAsync();

        return location.Id;
    }

    /// <summary>
    /// A venue that has never set hours relies on the switch alone. Reading "no timetable" as
    /// "never open" would silently stop every existing venue the day this shipped.
    /// </summary>
    [TestMethod]
    public async Task NoHoursSet_IsOpenWheneverTheSwitchIsOn()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedVenueAsync(options);

        Result<OrderingAvailability> result = await GetService(options).GetAsync(locationId);

        Assert.IsTrue(result.Data!.IsOpen);
    }

    /// <summary>The switch is how staff shut a swamped kitchen without editing the timetable.</summary>
    [TestMethod]
    public async Task SwitchOff_ClosesEvenInsideOpeningHours()
    {
        DateTime now = NowAtVenue();
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedVenueAsync(options, orderingEnabled: false,
            (now.DayOfWeek, new TimeOnly(0, 0), new TimeOnly(23, 59)));

        Result<OrderingAvailability> result = await GetService(options).GetAsync(locationId);

        Assert.IsFalse(result.Data!.IsOpen);
        StringAssert.Contains(result.Data.Message, "order at the till");
    }

    [TestMethod]
    public async Task InsideAWindow_IsOpen()
    {
        DateTime now = NowAtVenue();
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedVenueAsync(options, true,
            (now.DayOfWeek, now.AddHours(-1).TimeOfDay.ToTimeOnly(), now.AddHours(1).TimeOfDay.ToTimeOnly()));

        Assert.IsTrue((await GetService(options).GetAsync(locationId)).Data!.IsOpen);
    }

    [TestMethod]
    public async Task OutsideEveryWindow_IsClosed()
    {
        DateTime now = NowAtVenue();
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        // A window that both starts and ends before now, on today.
        TimeOnly opens = now.AddHours(-4).TimeOfDay.ToTimeOnly();
        TimeOnly closes = now.AddHours(-3).TimeOfDay.ToTimeOnly();

        int locationId = await SeedVenueAsync(options, true, (now.DayOfWeek, opens, closes));

        Result<OrderingAvailability> result = await GetService(options).GetAsync(locationId);

        Assert.IsFalse(result.Data!.IsOpen);
    }

    /// <summary>
    /// Closing time is exclusive: a kitchen that advertises "until 5" stops at 5, not after it.
    /// </summary>
    [TestMethod]
    public async Task AtTheClosingMinute_IsClosed()
    {
        DateTime now = NowAtVenue();
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        TimeOnly nowTime = TimeOnly.FromDateTime(now);
        int locationId = await SeedVenueAsync(options, true,
            (now.DayOfWeek, nowTime.AddHours(-2), nowTime));

        Assert.IsFalse((await GetService(options).GetAsync(locationId)).Data!.IsOpen);
    }

    /// <summary>
    /// A venue that shuts between lunch and dinner has two windows on one day, and must be open
    /// in both rather than appearing open all afternoon.
    /// </summary>
    [TestMethod]
    public async Task TwoWindowsInADay_TheGapBetweenThemIsClosed()
    {
        DateTime now = NowAtVenue();
        TimeOnly nowTime = TimeOnly.FromDateTime(now);
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        // Now sits in the gap between the two windows.
        int locationId = await SeedVenueAsync(options, true,
            (now.DayOfWeek, nowTime.AddHours(-4), nowTime.AddHours(-2)),
            (now.DayOfWeek, nowTime.AddHours(2), nowTime.AddHours(4)));

        Result<OrderingAvailability> result = await GetService(options).GetAsync(locationId);

        Assert.IsFalse(result.Data!.IsOpen);

        // And it knows the afternoon window is the next one.
        Assert.IsNotNull(result.Data.NextOpensAtLocal);
        Assert.AreEqual(now.Date, result.Data.NextOpensAtLocal!.Value.Date);
    }

    [TestMethod]
    public async Task WhenClosed_TheMessageSaysWhenItOpensAgain()
    {
        DateTime now = NowAtVenue();
        TimeOnly nowTime = TimeOnly.FromDateTime(now);
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedVenueAsync(options, true,
            (now.DayOfWeek, nowTime.AddHours(2), nowTime.AddHours(4)));

        Result<OrderingAvailability> result = await GetService(options).GetAsync(locationId);

        Assert.IsFalse(result.Data!.IsOpen);
        StringAssert.Contains(result.Data.Message, "opens again");
    }

    /// <summary>
    /// A window later in the week still has to be found, which is what the day-by-day walk is
    /// for - it has to cross the end of a month or year without special-casing either.
    /// </summary>
    [TestMethod]
    public async Task NextOpening_FindsAWindowLaterInTheWeek()
    {
        DateTime now = NowAtVenue();
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedVenueAsync(options, true,
            (now.AddDays(3).DayOfWeek, new TimeOnly(9, 0), new TimeOnly(17, 0)));

        Result<OrderingAvailability> result = await GetService(options).GetAsync(locationId);

        Assert.IsFalse(result.Data!.IsOpen);
        Assert.AreEqual(now.Date.AddDays(3), result.Data.NextOpensAtLocal!.Value.Date);
    }

    /// <summary>
    /// A venue created before time zones existed on Location must not end up reading its hours
    /// in UTC. The migration backfills them, and this is the assertion that the default is a
    /// real zone rather than the empty string EF scaffolds for a non-nullable string.
    /// </summary>
    [TestMethod]
    public void ANewLocationDefaultsToARealTimeZone()
    {
        Location location = new() { CompanyId = 1, Name = "Ipswich" };

        Assert.IsFalse(string.IsNullOrWhiteSpace(location.TimeZoneId));
        Assert.IsNotNull(TimeZoneInfo.FindSystemTimeZoneById(location.TimeZoneId));
    }

    /// <summary>
    /// A mistyped zone makes the hours an hour out; it must not take the venue offline. Failing
    /// closed here would punish a customer for an admin's typo.
    /// </summary>
    [TestMethod]
    public async Task AnUnknownTimeZone_FallsBackToUtcRatherThanClosing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int locationId = await SeedVenueAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            Location location = await ctx.Locations.FirstAsync(l => l.Id == locationId);
            location.TimeZoneId = "Not/AZone";
            ctx.LocationOpeningHours.Add(new LocationOpeningHours
            {
                LocationId = locationId,
                DayOfWeek = DateTime.UtcNow.DayOfWeek,
                OpensAt = new TimeOnly(0, 0),
                ClosesAt = new TimeOnly(23, 59),
                CreateDate = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        Result<OrderingAvailability> result = await GetService(options).GetAsync(locationId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Data!.IsOpen);
    }
}

internal static class TimeSpanExtensions
{
    public static TimeOnly ToTimeOnly(this TimeSpan value) => TimeOnly.FromTimeSpan(value);
}
