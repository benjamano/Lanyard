using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Parties;

[TestClass]
public class PartyBookingServiceTests
{
    private const string ActingUser = "acting-user";

    // Saturday 14 March 2026, before the clocks change, so venue-local time equals UTC.
    private static readonly DateOnly PartyDay = new(2026, 3, 14);

    private static PartyBookingService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options));

    private static PartyBooking NewBooking(Location location, Action<PartyBooking>? configure = null)
    {
        PartyBooking booking = new()
        {
            CompanyId = location.CompanyId,
            LocationId = location.Id,
            StartUtc = RotaTime.ToUtc(PartyDay, new TimeOnly(10, 30)),
            EndUtc = RotaTime.ToUtc(PartyDay, new TimeOnly(12, 30)),
            EatTimeUtc = RotaTime.ToUtc(PartyDay, new TimeOnly(11, 30)),
            PartyType = "Lazer + Play",
            ChildName = "Lily",
            ChildAgeTurning = 7,
            ContactName = "Sam Parent",
            ContactPhone = "07700 900123",
            ExpectedChildren = 15,
            MenuType = PartyMenuType.Hot
        };

        configure?.Invoke(booking);

        return booking;
    }

    private static async Task<PartyBooking> SeedBookingAsync(DbContextOptions<ApplicationDbContext> options, Location location, Action<PartyBooking>? configure = null)
    {
        Result<PartyBooking> result = await GetService(options).SaveBookingAsync(SchedulingTestHelpers.AdminScope, NewBooking(location, configure), ActingUser);

        Assert.IsTrue(result.IsSuccess, result.Error);

        return result.Data!;
    }

    [TestMethod]
    public async Task SaveBookingAsync_CreatesBookingForTheLocationsCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<PartyBooking> result = await GetService(options).SaveBookingAsync(SchedulingTestHelpers.ManagerScopeFor(location),
            NewBooking(location, x =>
            {
                x.CompanyId = 999;
                x.ChildName = "  Lily  ";
                x.ContactEmail = "  ";
                x.DepositPaidUtc = DateTime.UtcNow;
                x.Status = PartyBookingStatus.Cancelled;
            }), ActingUser);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        PartyBooking saved = await ctx.PartyBookings.SingleAsync();
        Assert.AreEqual(company.Id, saved.CompanyId);
        Assert.AreEqual("Lily", saved.ChildName);
        Assert.IsNull(saved.ContactEmail);
        Assert.AreEqual(PartyBookingStatus.Booked, saved.Status, "A new booking always starts as booked.");
        Assert.IsNull(saved.DepositPaidUtc, "Payments are recorded separately, never by the form.");
        Assert.AreEqual(ActingUser, saved.CreateByUserId);
    }

    [TestMethod]
    public async Task SaveBookingAsync_FailedCreateLeavesTheCallersBookingUntouched()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        PartyBooking booking = NewBooking(location, x => x.PartyHostUserId = "someone-elsewhere");

        Result<PartyBooking> result = await GetService(options).SaveBookingAsync(SchedulingTestHelpers.AdminScope, booking, ActingUser);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(Guid.Empty, booking.Id, "A retry after fixing the form must still create, not update.");
    }

    [TestMethod]
    public async Task SaveBookingAsync_RejectsManagerAtAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Result<PartyBooking> result = await GetService(options).SaveBookingAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation),
            NewBooking(location), ActingUser);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "your own location");
    }

    [TestMethod]
    public async Task SaveBookingAsync_RejectsStaffWithoutManagerRole()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<PartyBooking> result = await GetService(options).SaveBookingAsync(SchedulingTestHelpers.StaffScopeFor(location),
            NewBooking(location), ActingUser);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    [DataRow("child", "birthday child's name")]
    [DataRow("contact", "parent or guardian")]
    [DataRow("phone", "phone number")]
    [DataRow("email", "email address")]
    [DataRow("end", "finish after it starts")]
    [DataRow("eat", "eating time")]
    [DataRow("laser", "laser tag time")]
    [DataRow("children", "how many children")]
    [DataRow("deposit", "deposit can't be more")]
    public async Task SaveBookingAsync_RejectsInvalidBooking(string problem, string expectedError)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        PartyBooking booking = NewBooking(location, x =>
        {
            switch (problem)
            {
                case "child": x.ChildName = " "; break;
                case "contact": x.ContactName = ""; break;
                case "phone": x.ContactPhone = ""; break;
                case "email": x.ContactEmail = "not-an-email"; break;
                case "end": x.EndUtc = x.StartUtc; break;
                case "eat": x.EatTimeUtc = x.EndUtc.AddMinutes(30); break;
                case "laser": x.LaserTagTimeUtc = x.StartUtc.AddMinutes(-15); break;
                case "children": x.ExpectedChildren = 0; break;
                case "deposit": x.TotalPrice = 100m; x.DepositAmount = 150m; break;
            }
        });

        Result<PartyBooking> result = await GetService(options).SaveBookingAsync(SchedulingTestHelpers.AdminScope, booking, ActingUser);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, expectedError);
    }

    [TestMethod]
    public async Task SaveBookingAsync_RequiresHostToWorkAtTheLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile localHost = await SchedulingTestHelpers.SeedUserAsync(options, location, "Bradley");
        UserProfile otherHost = await SchedulingTestHelpers.SeedUserAsync(options, otherLocation, "Maddie");

        PartyBookingService service = GetService(options);

        Result<PartyBooking> rejected = await service.SaveBookingAsync(SchedulingTestHelpers.AdminScope,
            NewBooking(location, x => x.PartyHostUserId = otherHost.Id), ActingUser);
        Result<PartyBooking> accepted = await service.SaveBookingAsync(SchedulingTestHelpers.AdminScope,
            NewBooking(location, x => x.PartyHostUserId = localHost.Id), ActingUser);

        Assert.IsFalse(rejected.IsSuccess);
        StringAssert.Contains(rejected.Error, "party host");
        Assert.IsTrue(accepted.IsSuccess, accepted.Error);
        Assert.AreEqual(localHost.Id, accepted.Data!.PartyHostUserId);
    }

    [TestMethod]
    public async Task SaveBookingAsync_UpdateChangesDetailsButNotPaymentsOrStatus()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        PartyBooking booking = await SeedBookingAsync(options, location);
        PartyBookingService service = GetService(options);

        await service.SetDepositPaidAsync(SchedulingTestHelpers.AdminScope, booking.Id, true, ActingUser);

        // A form loaded before the deposit was paid still has no payment date on it.
        PartyBooking staleForm = NewBooking(location, x =>
        {
            x.Id = booking.Id;
            x.ChildName = "Harrison";
            x.ExpectedChildren = 16;
            x.Room = "Party room 2";
            x.Status = PartyBookingStatus.Cancelled;
        });

        Result<PartyBooking> result = await service.SaveBookingAsync(SchedulingTestHelpers.ManagerScopeFor(location), staleForm, "editor");

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        PartyBooking saved = await ctx.PartyBookings.SingleAsync();
        Assert.AreEqual("Harrison", saved.ChildName);
        Assert.AreEqual(16, saved.ExpectedChildren);
        Assert.AreEqual("Party room 2", saved.Room);
        Assert.IsNotNull(saved.DepositPaidUtc, "Saving the form must not undo a recorded deposit.");
        Assert.AreEqual(PartyBookingStatus.Booked, saved.Status);
        Assert.AreEqual("editor", saved.UpdateByUserId);
        Assert.AreEqual(ActingUser, saved.CreateByUserId);
    }

    [TestMethod]
    public async Task SaveBookingAsync_UpdateRejectsManagerOfAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        PartyBooking booking = await SeedBookingAsync(options, location);

        Location secondLocation;
        await using (ApplicationDbContext ctx = new(options))
        {
            secondLocation = new Location { CompanyId = company.Id, Name = "Colchester", IsActive = true };
            ctx.Locations.Add(secondLocation);
            await ctx.SaveChangesAsync();
        }

        // A manager at the second location tries to pull the first location's party over to theirs.
        Result<PartyBooking> result = await GetService(options).SaveBookingAsync(SchedulingTestHelpers.ManagerScopeFor(secondLocation),
            NewBooking(secondLocation, x => x.Id = booking.Id), ActingUser);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetBookingsAsync_ReturnsLocationsPartiesInRangeInOrder()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        await SeedBookingAsync(options, location, x =>
        {
            x.ChildName = "Late";
            x.StartUtc = RotaTime.ToUtc(PartyDay, new TimeOnly(15, 0));
            x.EndUtc = RotaTime.ToUtc(PartyDay, new TimeOnly(17, 0));
            x.EatTimeUtc = null;
        });
        await SeedBookingAsync(options, location, x => x.ChildName = "Early");
        await SeedBookingAsync(options, location, x =>
        {
            x.ChildName = "NextWeek";
            x.StartUtc = x.StartUtc.AddDays(7);
            x.EndUtc = x.EndUtc.AddDays(7);
            x.EatTimeUtc = null;
        });
        await SeedBookingAsync(options, otherLocation, x => x.ChildName = "Elsewhere");

        Result<List<PartyBooking>> result = await GetService(options).GetBookingsAsync(SchedulingTestHelpers.ManagerScopeFor(location), location.Id, PartyDay, PartyDay);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(new[] { "Early", "Late" }, result.Data!.Select(x => x.ChildName).ToArray());
    }

    [TestMethod]
    public async Task GetBookingsAsync_UsesVenueLocalDays()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        // 00:30 on 4 July in the UK is 23:30 UTC on 3 July: it belongs to 4 July.
        DateOnly summerDay = new(2026, 7, 4);
        await SeedBookingAsync(options, location, x =>
        {
            x.StartUtc = RotaTime.ToUtc(summerDay, new TimeOnly(0, 30));
            x.EndUtc = RotaTime.ToUtc(summerDay, new TimeOnly(2, 0));
            x.EatTimeUtc = null;
        });

        PartyBookingService service = GetService(options);
        Result<List<PartyBooking>> onTheDay = await service.GetBookingsAsync(SchedulingTestHelpers.AdminScope, location.Id, summerDay, summerDay);
        Result<List<PartyBooking>> dayBefore = await service.GetBookingsAsync(SchedulingTestHelpers.AdminScope, location.Id, summerDay.AddDays(-1), summerDay.AddDays(-1));

        Assert.AreEqual(1, onTheDay.Data!.Count);
        Assert.AreEqual(0, dayBefore.Data!.Count);
    }

    [TestMethod]
    public async Task GetBookingsAsync_HidesCancelledUnlessAsked()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        PartyBooking booking = await SeedBookingAsync(options, location);
        PartyBookingService service = GetService(options);

        await service.SetCancelledAsync(SchedulingTestHelpers.AdminScope, booking.Id, true, ActingUser);

        Result<List<PartyBooking>> hidden = await service.GetBookingsAsync(SchedulingTestHelpers.AdminScope, location.Id, PartyDay, PartyDay);
        Result<List<PartyBooking>> shown = await service.GetBookingsAsync(SchedulingTestHelpers.AdminScope, location.Id, PartyDay, PartyDay, includeCancelled: true);

        Assert.AreEqual(0, hidden.Data!.Count);
        Assert.AreEqual(1, shown.Data!.Count);
        Assert.AreEqual(PartyBookingStatus.Cancelled, shown.Data[0].Status);
    }

    [TestMethod]
    public async Task GetBookingsAsync_RejectsManagerAtAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Result<List<PartyBooking>> result = await GetService(options).GetBookingsAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation), location.Id, PartyDay, PartyDay);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetBookingAsync_RejectsManagerAtAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        PartyBooking booking = await SeedBookingAsync(options, location);

        PartyBookingService service = GetService(options);
        Result<PartyBooking> denied = await service.GetBookingAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation), booking.Id);
        Result<PartyBooking> allowed = await service.GetBookingAsync(SchedulingTestHelpers.ManagerScopeFor(location), booking.Id);

        Assert.IsFalse(denied.IsSuccess);
        Assert.IsTrue(allowed.IsSuccess, allowed.Error);
    }

    [TestMethod]
    public async Task SetBalancePaidAsync_AlsoMarksDepositPaid_AndUndoingDepositClearsBalance()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        PartyBooking booking = await SeedBookingAsync(options, location, x =>
        {
            x.TotalPrice = 180m;
            x.DepositAmount = 25m;
        });
        PartyBookingService service = GetService(options);

        Result<PartyBooking> paid = await service.SetBalancePaidAsync(SchedulingTestHelpers.AdminScope, booking.Id, true, ActingUser);

        Assert.AreEqual(PartyPaymentStatus.PaidInFull, paid.Data!.PaymentStatus);
        Assert.IsNotNull(paid.Data.DepositPaidUtc);
        Assert.AreEqual(0m, paid.Data.BalanceDue);

        Result<PartyBooking> undone = await service.SetDepositPaidAsync(SchedulingTestHelpers.AdminScope, booking.Id, false, ActingUser);

        Assert.AreEqual(PartyPaymentStatus.Unpaid, undone.Data!.PaymentStatus);
        Assert.IsNull(undone.Data.BalancePaidUtc);
        Assert.AreEqual(180m, undone.Data.BalanceDue);
    }

    [TestMethod]
    public async Task SetDepositPaidAsync_LeavesBalanceDueAfterDeposit()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        PartyBooking booking = await SeedBookingAsync(options, location, x =>
        {
            x.TotalPrice = 180m;
            x.DepositAmount = 25m;
        });

        Result<PartyBooking> result = await GetService(options).SetDepositPaidAsync(SchedulingTestHelpers.ManagerScopeFor(location), booking.Id, true, ActingUser);

        Assert.AreEqual(PartyPaymentStatus.DepositPaid, result.Data!.PaymentStatus);
        Assert.AreEqual(155m, result.Data.BalanceDue);
        Assert.AreEqual(ActingUser, result.Data.UpdateByUserId);
    }

    [TestMethod]
    public async Task SetCancelledAsync_RejectsManagerAtAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        PartyBooking booking = await SeedBookingAsync(options, location);

        Result<PartyBooking> result = await GetService(options).SetCancelledAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation), booking.Id, true, ActingUser);

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(PartyBookingStatus.Booked, (await ctx.PartyBookings.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task GetPartyTypeSuggestionsAsync_MostUsedFirst()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        await SeedBookingAsync(options, location, x => x.PartyType = "Play");
        await SeedBookingAsync(options, location, x => x.PartyType = "Lazer + Play");
        await SeedBookingAsync(options, location, x => x.PartyType = "Lazer + Play");
        await SeedBookingAsync(options, location, x => x.PartyType = "");

        Result<List<string>> result = await GetService(options).GetPartyTypeSuggestionsAsync(SchedulingTestHelpers.AdminScope, location.Id);

        CollectionAssert.AreEqual(new[] { "Lazer + Play", "Play" }, result.Data!.ToArray());
    }
}
