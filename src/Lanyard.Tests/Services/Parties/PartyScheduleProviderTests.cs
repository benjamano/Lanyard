using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Parties;

[TestClass]
public class PartyScheduleProviderTests
{
    private const int CompanyId = 1;
    private const int OtherCompanyId = 2;
    private const int LocationId = 10;

    // A Saturday in winter (UTC == UK time) and one in summer (UK = UTC+1).
    private static readonly DateTime WinterNoon = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SummerNoon = new(2026, 7, 18, 11, 0, 0, DateTimeKind.Utc);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static (PartyScheduleProvider Provider, ManualClock Clock, Mock<ISystemDbContextFactory> Factory) GetProvider(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<ISystemDbContextFactory> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));
        ManualClock clock = new(DateTimeOffset.UtcNow);

        return (new PartyScheduleProvider(factory.Object, clock), clock, factory);
    }

    private static async Task SeedLocationAsync(DbContextOptions<ApplicationDbContext> options, int id = LocationId, int companyId = CompanyId, bool active = true)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.Locations.Add(new Location { Id = id, CompanyId = companyId, Name = $"Location {id}", IsActive = active });
        await ctx.SaveChangesAsync();
    }

    private static async Task<PartyBooking> SeedPartyAsync(
        DbContextOptions<ApplicationDbContext> options,
        DateTime startUtc,
        int companyId = CompanyId,
        int locationId = LocationId,
        PartyBookingStatus status = PartyBookingStatus.Booked,
        bool provisional = false,
        PrivateHireKind? hire = null)
    {
        PartyBooking booking = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            LocationId = locationId,
            StartUtc = startUtc,
            EndUtc = startUtc.AddHours(2),
            ChildName = "Ava",
            ChildAgeTurning = 7,
            Room = "Room 2",
            Status = status,
            ExternalIsProvisional = provisional,
            HireKind = hire,
            CreateDate = DateTime.UtcNow
        };

        await using ApplicationDbContext ctx = new(options);
        ctx.PartyBookings.Add(booking);
        await ctx.SaveChangesAsync();

        return booking;
    }

    private static async Task<IReadOnlyList<PartySlot>> GetAsync(PartyScheduleProvider provider, DateTime nowUtc, int companyId = CompanyId)
    {
        Result<IReadOnlyList<PartySlot>> result = await provider.GetDayAsync(companyId, LocationId, nowUtc, CancellationToken.None);
        Assert.IsTrue(result.IsSuccess, result.Error);
        return result.Data!;
    }

    [TestMethod]
    public async Task ReturnsOnlyBookedPartiesThatGoAhead()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedLocationAsync(options);
        PartyBooking booked = await SeedPartyAsync(options, WinterNoon.AddHours(2));
        await SeedPartyAsync(options, WinterNoon.AddHours(2), status: PartyBookingStatus.Cancelled);
        await SeedPartyAsync(options, WinterNoon.AddHours(2), provisional: true);
        await SeedPartyAsync(options, WinterNoon.AddHours(2), hire: PrivateHireKind.PlayOnly);

        IReadOnlyList<PartySlot> slots = await GetAsync(GetProvider(options).Provider, WinterNoon);

        Assert.HasCount(1, slots);
        Assert.AreEqual(booked.Id, slots[0].Id);
        Assert.AreEqual("Room 2", slots[0].Room);
    }

    [TestMethod]
    public async Task AnotherCompanysLocation_ReturnsNothing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedLocationAsync(options);
        await SeedPartyAsync(options, WinterNoon.AddHours(2));

        Assert.IsEmpty(await GetAsync(GetProvider(options).Provider, WinterNoon, OtherCompanyId));
    }

    [TestMethod]
    public async Task AClosedLocation_ReturnsNothing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedLocationAsync(options, active: false);
        await SeedPartyAsync(options, WinterNoon.AddHours(2));

        Assert.IsEmpty(await GetAsync(GetProvider(options).Provider, WinterNoon));
    }

    [TestMethod]
    public async Task PartiesSwitchedOff_ReturnsNothing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedLocationAsync(options);
        await SeedPartyAsync(options, WinterNoon.AddHours(2));

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = CompanyId, Feature = CompanyFeature.Parties, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        Assert.IsEmpty(await GetAsync(GetProvider(options).Provider, WinterNoon));
    }

    [TestMethod]
    public async Task OnlyTodaysParties_PlusTheTwoHoursAfterMidnight_InSummerTime()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedLocationAsync(options);

        // UK midnight in July is 23:00 UTC the evening before.
        DateTime ukMidnightTonight = new(2026, 7, 18, 23, 0, 0, DateTimeKind.Utc);
        PartyBooking yesterday = await SeedPartyAsync(options, ukMidnightTonight.AddDays(-1).AddHours(-3)); // ended 22:00 UK yesterday
        PartyBooking today = await SeedPartyAsync(options, SummerNoon.AddHours(3));
        PartyBooking justAfterMidnight = await SeedPartyAsync(options, ukMidnightTonight.AddMinutes(90));
        PartyBooking tomorrowMorning = await SeedPartyAsync(options, ukMidnightTonight.AddHours(10));

        List<Guid> ids = [.. (await GetAsync(GetProvider(options).Provider, SummerNoon)).Select(s => s.Id)];

        CollectionAssert.AreEquivalent(new[] { today.Id, justAfterMidnight.Id }, ids);
        CollectionAssert.DoesNotContain(ids, yesterday.Id);
        CollectionAssert.DoesNotContain(ids, tomorrowMorning.Id);
    }

    [TestMethod]
    public async Task CachesForAMinute_AndInvalidateRefetches()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedLocationAsync(options);
        await SeedPartyAsync(options, WinterNoon.AddHours(2));

        (PartyScheduleProvider provider, ManualClock clock, _) = GetProvider(options);

        Assert.HasCount(1, await GetAsync(provider, WinterNoon));

        await SeedPartyAsync(options, WinterNoon.AddHours(3));
        Assert.HasCount(1, await GetAsync(provider, WinterNoon), "Still cached.");

        provider.Invalidate(LocationId);
        Assert.HasCount(2, await GetAsync(provider, WinterNoon), "A booking save clears the cache.");

        await SeedPartyAsync(options, WinterNoon.AddHours(4));
        clock.Now += PartyScheduleProvider.CacheDuration + TimeSpan.FromSeconds(1);
        Assert.HasCount(3, await GetAsync(provider, WinterNoon), "The cache runs out after a minute.");
    }

    [TestMethod]
    public async Task InvalidateDuringARead_IsNotLost()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedLocationAsync(options);
        await SeedPartyAsync(options, WinterNoon.AddHours(2));

        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Mock<ISystemDbContextFactory> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.SetResult();
                    await release.Task;
                }

                return new ApplicationDbContext(options);
            });

        PartyScheduleProvider provider = new(factory.Object, TimeProvider.System);

        Task<IReadOnlyList<PartySlot>> first = GetAsync(provider, WinterNoon);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.Invalidate(LocationId);
        release.SetResult();
        await first;

        await SeedPartyAsync(options, WinterNoon.AddHours(3));
        Assert.HasCount(2, await GetAsync(provider, WinterNoon), "The save made mid-read keeps the next read fresh.");
    }

    [TestMethod]
    public async Task ADatabaseError_IsAFailedResult()
    {
        Mock<ISystemDbContextFactory> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("down"));

        Result<IReadOnlyList<PartySlot>> result = await new PartyScheduleProvider(factory.Object, TimeProvider.System)
            .GetDayAsync(CompanyId, LocationId, WinterNoon, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "down");
    }
}
