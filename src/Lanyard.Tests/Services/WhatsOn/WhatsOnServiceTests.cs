using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.WhatsOn;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.WhatsOn;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.WhatsOn;

[TestClass]
public class WhatsOnServiceTests
{
    // Saturday 14 March 2026, before the clocks change, so venue-local time equals UTC.
    private static readonly DateOnly Day = new(2026, 3, 14);

    private static WhatsOnService GetService(DbContextOptions<ApplicationDbContext> options, params IWhatsOnSource[] sources) =>
        new(SchedulingTestHelpers.GetFactory(options), sources, NullLogger<WhatsOnService>.Instance);

    private static IWhatsOnSource Source(params WhatsOnItem[] items)
    {
        Mock<IWhatsOnSource> mock = new();
        mock.Setup(x => x.GetItemsAsync(It.IsAny<LocationScope>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<bool>()))
            .ReturnsAsync(Result<List<WhatsOnItem>>.Ok(items.ToList()));

        return mock.Object;
    }

    private static WhatsOnItem Item(DateTime startUtc, int children = 10, bool cancelled = false, PartyPaymentStatus? payment = PartyPaymentStatus.Unpaid,
        decimal? balance = null, PartyMenuType? menu = PartyMenuType.Hot, int? adults = null, bool provisional = false) => new()
    {
        Kind = WhatsOnKind.Party,
        Id = Guid.NewGuid(),
        Title = "Party",
        StartUtc = startUtc,
        EndUtc = startUtc.AddHours(2),
        Children = children,
        Adults = adults,
        IsCancelled = cancelled,
        PaymentStatus = payment,
        BalanceDue = balance,
        MenuType = menu,
        IsProvisional = provisional
    };

    [TestMethod]
    public async Task GetRangeAsync_BucketsByVenueLocalDayAcrossTheClockChange_AndIncludesEmptyDays()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        // The clocks go back on Sunday 25 October 2026. 23:30 BST on the Saturday is 22:30 UTC
        // the same day; 00:30 BST on the Sunday is 23:30 UTC on the Saturday.
        DateOnly saturday = new(2026, 10, 24);
        WhatsOnItem lateSaturday = Item(RotaTime.ToUtc(saturday, new TimeOnly(23, 30)));
        WhatsOnItem earlySunday = Item(RotaTime.ToUtc(saturday.AddDays(1), new TimeOnly(0, 30)));

        Result<WhatsOnRange> result = await GetService(options, Source(lateSaturday, earlySunday))
            .GetRangeAsync(SchedulingTestHelpers.ManagerScopeFor(location), location.Id, saturday, saturday.AddDays(2));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(3, result.Data!.Days.Count, "Every day in the range, busy or not.");
        CollectionAssert.AreEqual(new[] { lateSaturday.Id }, result.Data.Days[0].Items.Select(x => x.Id).ToArray());
        CollectionAssert.AreEqual(new[] { earlySunday.Id }, result.Data.Days[1].Items.Select(x => x.Id).ToArray());
        Assert.AreEqual(0, result.Data.Days[2].Items.Count);
        Assert.AreEqual(WhatsOnTotals.Empty, result.Data.Days[2].Totals);
    }

    [TestMethod]
    public async Task GetRangeAsync_TotalsSkipCancelledEntries()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        DateTime ten = RotaTime.ToUtc(Day, new TimeOnly(10, 0));

        Result<WhatsOnRange> result = await GetService(options, Source(
                Item(ten, children: 12, adults: 4, payment: PartyPaymentStatus.DepositPaid, balance: 150m, menu: PartyMenuType.Hot),
                Item(ten.AddHours(1), children: 8, payment: PartyPaymentStatus.PaidInFull, balance: 0m, menu: PartyMenuType.Cold, provisional: true),
                Item(ten.AddHours(3), children: 30, menu: null, payment: PartyPaymentStatus.Unpaid),
                Item(ten.AddHours(4), children: 99, adults: 9, cancelled: true, balance: 500m)))
            .GetRangeAsync(SchedulingTestHelpers.AdminScope, location.Id, Day, Day, includeCancelled: true);

        Assert.IsTrue(result.IsSuccess, result.Error);
        WhatsOnDay day = result.Data!.Days.Single();
        Assert.AreEqual(4, day.Items.Count, "Cancelled entries are still listed when asked for.");
        Assert.AreEqual(new WhatsOnTotals(Items: 3, Children: 50, Adults: 4, HotFood: 1, ColdFood: 1, Unpaid: 2, BalanceDue: 150m, Provisional: 1), day.Totals);
        Assert.AreEqual(day.Totals, result.Data.Totals);
    }

    [TestMethod]
    public async Task GetRangeAsync_AFailingSourceBecomesAWarning_AndTheRestStillShows()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Mock<IWhatsOnSource> failing = new();
        failing.Setup(x => x.GetItemsAsync(It.IsAny<LocationScope>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<bool>()))
            .ReturnsAsync(Result<List<WhatsOnItem>>.Fail("Bookings couldn't be loaded."));

        WhatsOnItem party = Item(RotaTime.ToUtc(Day, new TimeOnly(10, 0)));

        Result<WhatsOnRange> result = await GetService(options, failing.Object, Source(party))
            .GetRangeAsync(SchedulingTestHelpers.AdminScope, location.Id, Day, Day);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(new[] { "Bookings couldn't be loaded." }, result.Data!.Warnings);
        Assert.AreEqual(party.Id, result.Data.Days.Single().Items.Single().Id);
    }

    [TestMethod]
    public async Task GetRangeAsync_RefusesAManagerAtAnotherLocation_AndAnOverlongRange()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location other) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other", "Elsewhere");

        WhatsOnService service = GetService(options, Source());

        Result<WhatsOnRange> otherLocation = await service.GetRangeAsync(SchedulingTestHelpers.ManagerScopeFor(location), other.Id, Day, Day);
        Result<WhatsOnRange> staff = await service.GetRangeAsync(SchedulingTestHelpers.StaffScopeFor(location), location.Id, Day, Day);
        Result<WhatsOnRange> tooLong = await service.GetRangeAsync(SchedulingTestHelpers.AdminScope, location.Id, Day, Day.AddDays(WhatsOnService.MaxDays));
        Result<WhatsOnRange> backwards = await service.GetRangeAsync(SchedulingTestHelpers.AdminScope, location.Id, Day, Day.AddDays(-1));

        Assert.IsFalse(otherLocation.IsSuccess);
        Assert.IsFalse(staff.IsSuccess, "Plain staff don't get What's on.");
        Assert.IsFalse(tooLong.IsSuccess);
        Assert.IsFalse(backwards.IsSuccess);
    }

    [TestMethod]
    public async Task GetDashboardDayAsync_LoadsTheDayForTheDashboardsCompany_WithAScopeTheSourcesAccept()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        WhatsOnItem party = Item(RotaTime.ToUtc(Day, new TimeOnly(10, 0)));
        LocationScope? seen = null;

        Mock<IWhatsOnSource> source = new();
        source.Setup(x => x.GetItemsAsync(It.IsAny<LocationScope>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<bool>()))
            .Callback<LocationScope, int, int, DateOnly, DateOnly, bool>((scope, _, _, _, _, _) => seen = scope)
            .ReturnsAsync(Result<List<WhatsOnItem>>.Ok([party]));

        Result<WhatsOnDay> result = await GetService(options, source.Object).GetDashboardDayAsync(company.Id, location.Id, Day);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(Day, result.Data!.Date);
        Assert.AreEqual(party.Id, result.Data.Items.Single().Id);
        Assert.IsNotNull(seen);
        Assert.IsTrue(seen.IsAdmin, "The sources check the scope, so the dashboard read must pass it.");
        Assert.AreEqual(company.Id, seen.CompanyId);
    }

    [TestMethod]
    public async Task GetDashboardDayAsync_RefusesALocationFromAnotherCompany_OrOneThatsClosed()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location other) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other", "Elsewhere");

        await using (ApplicationDbContext ctx = new(options))
        {
            Location closed = new() { CompanyId = company.Id, Name = "Closed", IsActive = false };
            ctx.Locations.Add(closed);
            await ctx.SaveChangesAsync();

            WhatsOnService service = GetService(options, Source());

            Assert.IsFalse((await service.GetDashboardDayAsync(company.Id, other.Id, Day)).IsSuccess, "Another company's location.");
            Assert.IsFalse((await service.GetDashboardDayAsync(company.Id, closed.Id, Day)).IsSuccess, "An inactive location.");
            Assert.IsFalse((await service.GetDashboardDayAsync(company.Id, 999_999, Day)).IsSuccess, "A missing location.");
            Assert.IsTrue((await service.GetDashboardDayAsync(company.Id, location.Id, Day)).IsSuccess);
        }
    }

    [TestMethod]
    public async Task GetRangeAsync_ListsOnlyPublishedStaffedShifts_AsTheyWerePublished()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ben");
        UserProfile cal = await SchedulingTestHelpers.SeedUserAsync(options, location, "Cal");
        StaffPosition host = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Party Host");

        DateTime nine = RotaTime.ToUtc(Day, new TimeOnly(9, 0));

        await using (ApplicationDbContext ctx = new(options))
        {
            // Published 09:00-17:00 as Party Host, since moved to 12:00-20:00 in a draft the person hasn't been told about.
            Shift published = NewShift(location, amy.Id, nine.AddHours(3), nine.AddHours(11));
            published.PublishedDateUtc = nine.AddDays(-7);
            published.PublishedStartUtc = nine;
            published.PublishedEndUtc = nine.AddHours(8);
            published.PublishedStaffPositionId = host.Id;
            published.UpdateDate = nine.AddDays(-1);

            // A draft nobody's been told about.
            Shift draft = NewShift(location, ben.Id, nine, nine.AddHours(4));

            // Published, then removed and the person told.
            Shift removed = NewShift(location, cal.Id, nine, nine.AddHours(4));
            removed.MarkPublished(nine.AddDays(-7), "manager");
            removed.IsActive = false;

            // Published open shift: nobody on it.
            Shift open = NewShift(location, null, nine, nine.AddHours(4));
            open.MarkPublished(nine.AddDays(-7), "manager");

            ctx.Shifts.AddRange(published, draft, removed, open);
            await ctx.SaveChangesAsync();
        }

        Result<WhatsOnRange> result = await GetService(options, Source())
            .GetRangeAsync(SchedulingTestHelpers.ManagerScopeFor(location), location.Id, Day, Day);

        Assert.IsTrue(result.IsSuccess, result.Error);
        WhatsOnStaff only = result.Data!.Days.Single().Staff.Single();
        Assert.AreEqual(amy.Id, only.UserId);
        Assert.AreEqual("Amy Tester", only.Name);
        Assert.AreEqual("Party Host", only.Position);
        Assert.AreEqual(nine, only.StartUtc, "Shown as last published, not as the unpublished draft.");
        Assert.AreEqual(nine.AddHours(8), only.EndUtc);
    }

    private static Shift NewShift(Location location, string? userId, DateTime startUtc, DateTime endUtc) => new()
    {
        Id = Guid.NewGuid(),
        LocationId = location.Id,
        UserId = userId,
        StartUtc = startUtc,
        EndUtc = endUtc,
        CreateDate = startUtc.AddDays(-14),
        CreateByUserId = "manager"
    };
}
