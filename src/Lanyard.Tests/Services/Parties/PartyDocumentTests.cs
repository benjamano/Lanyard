using System.Text;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Common;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Parties;

[TestClass]
public class PartyDocumentTests
{
    private static readonly DateOnly PartyDay = new(2026, 3, 14);

    private static PartyBooking Booking(Action<PartyBooking>? configure = null)
    {
        PartyBooking booking = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = 1,
            LocationId = 1,
            StartUtc = RotaTime.ToUtc(PartyDay, new TimeOnly(10, 30)),
            EndUtc = RotaTime.ToUtc(PartyDay, new TimeOnly(12, 30)),
            EatTimeUtc = RotaTime.ToUtc(PartyDay, new TimeOnly(11, 30)),
            PartyType = "Lazer + Play",
            ChildName = "Lily",
            ChildAgeTurning = 7,
            ContactName = "Sam Parent",
            ContactPhone = "07700 900123",
            ExpectedChildren = 15,
            AllergyNotes = "One child has a nut allergy",
            TotalPrice = 180m,
            DepositAmount = 25m
        };

        configure?.Invoke(booking);

        return booking;
    }

    private static List<PartyMenuItem> Menu() =>
    [
        new() { LocationId = 1, Kind = PartyMenuItemKind.HotMain, Name = "Sausage", Description = "with chips", AllergenText = "Contains: Gluten" },
        new() { LocationId = 1, Kind = PartyMenuItemKind.HotMain, Name = "Nuggets", Description = "with chips" },
        new() { LocationId = 1, Kind = PartyMenuItemKind.HotSide, Name = "Peas" },
        new() { LocationId = 1, Kind = PartyMenuItemKind.HotSide, Name = "Beans" },
        new() { LocationId = 1, Kind = PartyMenuItemKind.Sandwich, Name = "Ham", AllergenText = "No allergy information provided." },
        new() { LocationId = 1, Kind = PartyMenuItemKind.ColdExtra, Name = "Crisps", AllergenText = "May contain MILK." }
    ];

    private static byte[] Logo() =>
        Convert.FromBase64String(QrCodeDataUri.Create("logo", 4).Split(',')[1]);

    private static void AssertIsPdf(byte[] pdf)
    {
        Assert.IsTrue(pdf.Length > 1000, "Expected a real document.");
        Assert.AreEqual("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [TestMethod]
    [DataRow(PartyMenuType.Hot, PartyDocumentType.ReservedCard)]
    [DataRow(PartyMenuType.Hot, PartyDocumentType.FoodSheet)]
    [DataRow(PartyMenuType.Hot, PartyDocumentType.Pack)]
    [DataRow(PartyMenuType.Cold, PartyDocumentType.FoodSheet)]
    [DataRow(PartyMenuType.Cold, PartyDocumentType.Pack)]
    public void Render_ProducesEachDocument(PartyMenuType menu, PartyDocumentType type)
    {
        PartyDocumentModel model = new(Booking(x => x.MenuType = menu), "Amy Clarke", Menu(), Logo(), "#167a47");

        AssertIsPdf(PartyDocumentRenderer.Render(model, type));
    }

    // Layout limits that would only show up as a render exception: a long name on the table
    // card, a big party spilling the cold sheet onto a second page, and a location with no menu.
    [TestMethod]
    public void Render_CopesWithLongNamesBigPartiesAndNoMenu()
    {
        PartyDocumentModel bigCold = new(
            Booking(x =>
            {
                x.MenuType = PartyMenuType.Cold;
                x.ExpectedChildren = 80;
                x.ChildName = "Florence-May Alexandra Wilhelmina";
                x.AllergyNotes = null;
                x.EatTimeUtc = null;
            }),
            null, [], null, "#167a47");

        PartyDocumentModel manyMains = new(
            Booking(),
            null,
            [.. Enumerable.Range(1, 12).Select(i => new PartyMenuItem { LocationId = 1, Kind = PartyMenuItemKind.HotMain, Name = $"Meal {i}", AllergenText = "Contains: Gluten" }),
             .. Enumerable.Range(1, 4).Select(i => new PartyMenuItem { LocationId = 1, Kind = PartyMenuItemKind.HotSide, Name = $"Side {i}" })],
            null, "#167a47");

        AssertIsPdf(PartyDocumentRenderer.Render(bigCold, PartyDocumentType.Pack));
        AssertIsPdf(PartyDocumentRenderer.Render(manyMains, PartyDocumentType.FoodSheet));
        AssertIsPdf(PartyDocumentRenderer.Render(new PartyDocumentModel(Booking(), null, [], null, "#167a47"), PartyDocumentType.FoodSheet));
    }

    private static PartyDocumentService GetDocumentService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<ICompanyLocationService> locations = new();
        locations.Setup(x => x.GetCompanyBrandingForLocationAsync(It.IsAny<int>()))
            .ReturnsAsync(Result<CompanyBrandingInfo>.Ok(new CompanyBrandingInfo(1, "Play2Day", "#c8102e", null, null)));

        return new PartyDocumentService(
            new PartyBookingService(SchedulingTestHelpers.GetFactory(options)),
            new PartySettingsService(SchedulingTestHelpers.GetFactory(options)),
            locations.Object,
            new Mock<IFileService>().Object,
            NullLogger<PartyDocumentService>.Instance);
    }

    private static async Task<PartyBooking> SeedBookingAsync(DbContextOptions<ApplicationDbContext> options, Location location, PartyMenuType menu)
    {
        Result<PartyBooking> saved = await new PartyBookingService(SchedulingTestHelpers.GetFactory(options))
            .SaveBookingAsync(SchedulingTestHelpers.AdminScope, Booking(x =>
            {
                x.Id = Guid.Empty;
                x.CompanyId = location.CompanyId;
                x.LocationId = location.Id;
                x.MenuType = menu;
            }), "acting-user");

        Assert.IsTrue(saved.IsSuccess, saved.Error);

        return saved.Data!;
    }

    [TestMethod]
    public async Task BuildAsync_NamesTheFileAfterTheChildDateAndSheet()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        PartyBooking booking = await SeedBookingAsync(options, location, PartyMenuType.Cold);

        Result<PartyDocument> result = await GetDocumentService(options).BuildAsync(SchedulingTestHelpers.ManagerScopeFor(location), booking.Id, PartyDocumentType.FoodSheet);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Lily 2026-03-14 cold food sheet.pdf", result.Data!.FileName);
        AssertIsPdf(result.Data.Pdf);
    }

    [TestMethod]
    public async Task BuildAsync_RejectsManagerAtAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        PartyBooking booking = await SeedBookingAsync(options, location, PartyMenuType.Hot);

        Result<PartyDocument> result = await GetDocumentService(options).BuildAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation), booking.Id, PartyDocumentType.Pack);

        Assert.IsFalse(result.IsSuccess);
    }
}
