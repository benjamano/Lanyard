using Lanyard.Tests.TestUtilities;
using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Parties;
using Lanyard.Application.Services.WhatsOn;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.WhatsOn;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.WhatsOn;

[TestClass]
public class PartyWhatsOnSourceTests
{
    // Before the clocks change, so venue-local time equals UTC.
    private static readonly DateOnly Day = new(2026, 3, 14);

    private static PartyWhatsOnSource GetSource(DbContextOptions<ApplicationDbContext> options, bool partiesEnabled = true)
    {
        Mock<ICompanyFeatureService> features = new();
        features.Setup(x => x.IsEnabledForLocationAsync(It.IsAny<int>(), CompanyFeature.Parties)).ReturnsAsync(partiesEnabled);

        return new PartyWhatsOnSource(new PartyBookingService(SchedulingTestHelpers.GetFactory(options)), features.Object);
    }

    private static async Task<PartyBooking> SeedAsync(DbContextOptions<ApplicationDbContext> options, Location location, Action<PartyBooking> configure)
    {
        PartyBooking booking = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = location.CompanyId,
            LocationId = location.Id,
            StartUtc = RotaTime.ToUtc(Day, new TimeOnly(10, 0)),
            EndUtc = RotaTime.ToUtc(Day, new TimeOnly(12, 0)),
            ChildName = "Lily",
            ContactName = "Sam Parent",
            ContactPhone = "07700 900123",
            ExpectedChildren = 15,
            CreateDate = DateTime.UtcNow,
            CreateByUserId = "manager"
        };

        configure(booking);

        await using ApplicationDbContext ctx = new(options);
        ctx.PartyBookings.Add(booking);
        await ctx.SaveChangesAsync();

        return booking;
    }

    [TestMethod]
    public async Task GetItemsAsync_MapsAKidsParty()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile host = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");

        PartyBooking party = await SeedAsync(options, location, x =>
        {
            x.ChildAgeTurning = 7;
            x.PartyType = "Play and Laser Party";
            x.EatTimeUtc = x.StartUtc.AddHours(1);
            x.LaserTagTimeUtc = x.StartUtc.AddMinutes(30);
            x.ExpectedAdults = 6;
            x.Room = "Room 2";
            x.MenuType = PartyMenuType.Cold;
            x.AllergyNotes = "Nuts";
            x.PartyHostUserId = host.Id;
            x.TotalPrice = 200m;
            x.DepositAmount = 50m;
            x.DepositPaidUtc = DateTime.UtcNow;
            x.ExternalSource = "JustStepOnline";
            x.ExternalIsProvisional = true;
        });

        Result<List<WhatsOnItem>> result = await GetSource(options)
            .GetItemsAsync(SchedulingTestHelpers.ManagerScopeFor(location), location.CompanyId, location.Id, Day, Day, includeCancelled: false);

        Assert.IsTrue(result.IsSuccess, result.Error);
        WhatsOnItem item = result.Data!.Single();
        Assert.AreEqual(party.Id, item.Id);
        Assert.AreEqual("Lily, turning 7", item.Title);
        Assert.AreEqual("Play and Laser Party", item.Subtitle);
        Assert.AreEqual($"/manage/parties/{party.Id}", item.Href);
        CollectionAssert.AreEqual(new[] { "Eat", "Laser" }, item.Markers.Select(x => x.Label).ToArray());
        Assert.AreEqual(15, item.Children);
        Assert.AreEqual(6, item.Adults);
        Assert.IsFalse(item.CountsGuests);
        Assert.AreEqual("Room 2", item.Room);
        Assert.AreEqual("Amy Tester", item.HostName);
        Assert.AreEqual("Nuts", item.AllergyNotes);
        Assert.IsTrue(item.IsProvisional);
        Assert.AreEqual(PartyPaymentStatus.DepositPaid, item.PaymentStatus);
        Assert.AreEqual(150m, item.BalanceDue);
        Assert.AreEqual(PartyMenuType.Cold, item.MenuType);
    }

    [TestMethod]
    public async Task GetItemsAsync_APrivateHireHasNoFoodAndCountsGuests()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        await SeedAsync(options, location, x =>
        {
            x.HireKind = PrivateHireKind.LaserTagAndPlay;
            x.ChildName = string.Empty;
            x.ContactName = "Sam Jones";
            x.MenuType = PartyMenuType.Hot;
        });

        Result<List<WhatsOnItem>> result = await GetSource(options)
            .GetItemsAsync(SchedulingTestHelpers.AdminScope, location.CompanyId, location.Id, Day, Day, includeCancelled: false);

        WhatsOnItem item = result.Data!.Single();
        Assert.AreEqual("Private hire · Sam Jones", item.Title);
        Assert.AreEqual("Laser tag + Play", item.Subtitle);
        Assert.IsTrue(item.CountsGuests);
        Assert.IsNull(item.MenuType);
    }

    [TestMethod]
    public async Task GetItemsAsync_CancelledOnlyWhenAskedFor()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        await SeedAsync(options, location, x => x.Status = PartyBookingStatus.Cancelled);

        PartyWhatsOnSource source = GetSource(options);

        Result<List<WhatsOnItem>> hidden = await source.GetItemsAsync(SchedulingTestHelpers.AdminScope, location.CompanyId, location.Id, Day, Day, includeCancelled: false);
        Result<List<WhatsOnItem>> shown = await source.GetItemsAsync(SchedulingTestHelpers.AdminScope, location.CompanyId, location.Id, Day, Day, includeCancelled: true);

        Assert.AreEqual(0, hidden.Data!.Count);
        Assert.IsTrue(shown.Data!.Single().IsCancelled);
    }

    [TestMethod]
    public async Task GetItemsAsync_ReturnsNothingWhenTheCompanyHasNoParties()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        await SeedAsync(options, location, _ => { });

        Result<List<WhatsOnItem>> result = await GetSource(options, partiesEnabled: false)
            .GetItemsAsync(SchedulingTestHelpers.AdminScope, location.CompanyId, location.Id, Day, Day, includeCancelled: false);

        Assert.IsTrue(result.IsSuccess, "No feature is not a failure: there's just nothing to show.");
        Assert.AreEqual(0, result.Data!.Count);
    }
}
