using System.Text;
using Lanyard.Application.Services.Common;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Parties;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Parties;

[TestClass]
public class PartyTemplateTests
{
    private const string ActingUser = "acting-user";

    private static PartyTemplateService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options));

    private static PartyDocumentTemplate VoucherFor(Company company, Action<List<PartyTemplateField>>? editFields = null)
    {
        PartyDocumentTemplate template = PartyTemplateFields.Default(PartyArtworkDocument.Voucher, company.Id);
        List<PartyTemplateField> fields = PartyTemplateFields.Parse(template.FieldsJson);
        editFields?.Invoke(fields);
        template.FieldsJson = PartyTemplateFields.Serialize(fields);
        template.ArtworkFileId = Guid.NewGuid();

        return template;
    }

    [TestMethod]
    public async Task GetTemplateAsync_ReturnsTheDefaultLayoutWhenNoneSaved()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<PartyDocumentTemplate> voucher = await GetService(options).GetTemplateAsync(SchedulingTestHelpers.ManagerScopeFor(location), company.Id, PartyArtworkDocument.Voucher);
        Result<PartyDocumentTemplate> feedback = await GetService(options).GetTemplateAsync(SchedulingTestHelpers.ManagerScopeFor(location), company.Id, PartyArtworkDocument.FeedbackForm);

        Assert.IsTrue(voucher.IsSuccess, voucher.Error);
        Assert.AreEqual(Guid.Empty, voucher.Data!.Id);
        Assert.IsNull(voucher.Data.ArtworkFileId, "No artwork until one is uploaded - the standard design prints.");
        Assert.AreEqual(12, voucher.Data.VoucherValidityMonths);
        CollectionAssert.AreEquivalent(
            new[] { PartyTemplateFieldKind.PresentedTo, PartyTemplateFieldKind.PartyDate, PartyTemplateFieldKind.ExpiryDate, PartyTemplateFieldKind.AuthorisedBy },
            PartyTemplateFields.Parse(voucher.Data.FieldsJson).Select(x => x.Kind).ToArray());
        Assert.AreEqual(270, PartyTemplateFields.Parse(feedback.Data!.FieldsJson).Single(x => x.Kind == PartyTemplateFieldKind.PartyName).Rotation);
    }

    [TestMethod]
    public async Task SaveTemplateAsync_CreatesThenUpdatesOneRowPerCompanyAndDocument()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        PartyTemplateService service = GetService(options);
        LocationScope manager = SchedulingTestHelpers.ManagerScopeFor(location);

        await service.SaveTemplateAsync(manager, VoucherFor(company), ActingUser);

        PartyDocumentTemplate second = VoucherFor(company, fields => fields[0].XMm = 70);
        second.VoucherValidityMonths = 6;
        Result<PartyDocumentTemplate> result = await service.SaveTemplateAsync(manager, second, ActingUser);
        await service.SaveTemplateAsync(manager, PartyTemplateFields.Default(PartyArtworkDocument.FeedbackForm, company.Id), ActingUser);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(2, await ctx.PartyDocumentTemplates.CountAsync());
        PartyDocumentTemplate saved = await ctx.PartyDocumentTemplates.SingleAsync(x => x.Document == PartyArtworkDocument.Voucher);
        Assert.AreEqual(6, saved.VoucherValidityMonths);
        Assert.AreEqual(70, PartyTemplateFields.Parse(saved.FieldsJson)[0].XMm);
        Assert.AreEqual(ActingUser, saved.UpdatedByUserId);
    }

    [TestMethod]
    public async Task SaveTemplateAsync_RejectsManagerFromAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Result<PartyDocumentTemplate> saved = await GetService(options).SaveTemplateAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation), VoucherFor(company), ActingUser);
        Result<PartyDocumentTemplate> read = await GetService(options).GetTemplateAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation), company.Id, PartyArtworkDocument.Voucher);

        Assert.IsFalse(saved.IsSuccess);
        Assert.IsFalse(read.IsSuccess);
    }

    [TestMethod]
    [DataRow("size", "between 40 and 420")]
    [DataRow("months", "between 1 and 60 months")]
    [DataRow("offEdge", "off the edge")]
    [DataRow("tiny", "too small")]
    [DataRow("rotation", "90 or 270")]
    [DataRow("colour", "#222222")]
    public async Task SaveTemplateAsync_RejectsBadLayouts(string problem, string expected)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, _) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        PartyDocumentTemplate template = VoucherFor(company, fields =>
        {
            switch (problem)
            {
                case "offEdge": fields[0].XMm = 120; break;
                case "tiny": fields[0].HeightMm = 1; break;
                case "rotation": fields[0].Rotation = 45; break;
                case "colour": fields[0].ColourHex = "red"; break;
            }
        });

        if (problem == "size")
        {
            template.WidthMm = 20;
        }

        if (problem == "months")
        {
            template.VoucherValidityMonths = 0;
        }

        Result<PartyDocumentTemplate> result = await GetService(options).SaveTemplateAsync(SchedulingTestHelpers.AdminScope, template, ActingUser);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, expected);
    }

    [TestMethod]
    public void Parse_TreatsUnreadableJsonAsNoBoxes()
    {
        Assert.AreEqual(0, PartyTemplateFields.Parse("{not json").Count);
        Assert.AreEqual(0, PartyTemplateFields.Parse(null).Count);
    }

    // ---- Rendering ----

    private static readonly DateOnly PartyDay = new(2026, 3, 14);

    private static PartyBooking Booking() => new()
    {
        CompanyId = 1,
        LocationId = 1,
        ChildName = "Lily",
        ChildAgeTurning = 7,
        StartUtc = RotaTime.ToUtc(PartyDay, new TimeOnly(10, 30)),
        EndUtc = RotaTime.ToUtc(PartyDay, new TimeOnly(12, 30)),
        ExpectedChildren = 10
    };

    private static byte[] Image() => Convert.FromBase64String(QrCodeDataUri.Create("artwork", 4).Split(',')[1]);

    private static PartyArtwork Artwork(PartyArtworkDocument document, decimal width = 148, decimal height = 210, int months = 12)
    {
        PartyDocumentTemplate template = PartyTemplateFields.Default(document, 1);
        template.WidthMm = width;
        template.HeightMm = height;
        template.VoucherValidityMonths = months;

        return new PartyArtwork(template, PartyTemplateFields.Parse(template.FieldsJson), Image());
    }

    private static void AssertIsPdf(byte[] pdf, int pages)
    {
        Assert.AreEqual("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.AreEqual(pages, CountPages(pdf), "Unexpected number of sheets.");
    }

    // Counts "/Type /Page" objects (not "/Pages") - enough to tell how pieces were laid out.
    private static int CountPages(byte[] pdf) =>
        System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page[^s]").Count;

    [TestMethod]
    public void Render_PutsVoucherAndFeedbackOnOneSheetInThePack()
    {
        PartyDocumentModel model = new(Booking(), "Amy Clarke", [], null, "#167a47",
            Artwork(PartyArtworkDocument.Voucher), Artwork(PartyArtworkDocument.FeedbackForm), "Sam Manager", "Play2Day");

        // Reserved card, food sheet, then one sheet with both A5 pieces side by side.
        AssertIsPdf(PartyDocumentRenderer.Render(model, PartyDocumentType.Pack), 3);
        AssertIsPdf(PartyDocumentRenderer.Render(model, PartyDocumentType.Voucher), 1);
    }

    [TestMethod]
    public void Render_StandardDesignsWithoutArtwork()
    {
        PartyDocumentModel model = new(Booking(), null, [], null, "#167a47", AuthorisedBy: "Sam Manager", CompanyName: "Play2Day");

        AssertIsPdf(PartyDocumentRenderer.Render(model, PartyDocumentType.Voucher), 1);
        AssertIsPdf(PartyDocumentRenderer.Render(model, PartyDocumentType.FeedbackForm), 1);
        AssertIsPdf(PartyDocumentRenderer.Render(model, PartyDocumentType.Pack), 3);
    }

    [TestMethod]
    public void Render_LaysOutOtherArtworkSizes()
    {
        // Two A5 landscape pieces stack on one portrait sheet; A4 and oversized artwork get a sheet each.
        PartyDocumentModel landscape = new(Booking(), null, [], null, "#167a47",
            Artwork(PartyArtworkDocument.Voucher, 210, 148), Artwork(PartyArtworkDocument.FeedbackForm, 210, 148));
        PartyDocumentModel big = new(Booking(), null, [], null, "#167a47",
            Artwork(PartyArtworkDocument.Voucher, 297, 210), Artwork(PartyArtworkDocument.FeedbackForm, 420, 297));
        PartyDocumentModel mixed = new(Booking(), null, [], null, "#167a47",
            Artwork(PartyArtworkDocument.Voucher, 148, 210), Artwork(PartyArtworkDocument.FeedbackForm, 210, 148));

        AssertIsPdf(PartyDocumentRenderer.Render(landscape, PartyDocumentType.Pack), 3);
        AssertIsPdf(PartyDocumentRenderer.Render(big, PartyDocumentType.Pack), 4);
        AssertIsPdf(PartyDocumentRenderer.Render(mixed, PartyDocumentType.Pack), 4);
    }

    // A wide logo fitted only to its height used to overrun the fixed-width corner it sits in, and
    // QuestPDF fails the whole document on that rather than clipping.
    [TestMethod]
    public void Render_CopesWithAWideLogo()
    {
        using SkiaSharp.SKBitmap bitmap = new(900, 100);
        using SkiaSharp.SKImage image = SkiaSharp.SKImage.FromBitmap(bitmap);
        byte[] wideLogo = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90).ToArray();

        PartyDocumentModel model = new(Booking(), "Amy Clarke", [], wideLogo, "#c8102e", AuthorisedBy: "Sam Manager", CompanyName: "Play2Day");

        AssertIsPdf(PartyDocumentRenderer.Render(model, PartyDocumentType.Pack), 3);
    }

    [TestMethod]
    public void FieldValue_FillsEachBoxFromTheParty()
    {
        PartyDocumentModel model = new(Booking(), "Amy Clarke", [], null, "#167a47",
            Artwork(PartyArtworkDocument.Voucher, months: 6), null, "Sam Manager");

        Assert.AreEqual("Lily", PartyDocumentRenderer.FieldValue(PartyTemplateFieldKind.PresentedTo, model));
        Assert.AreEqual("14 March 2026", PartyDocumentRenderer.FieldValue(PartyTemplateFieldKind.PartyDate, model));
        Assert.AreEqual("14 September 2026", PartyDocumentRenderer.FieldValue(PartyTemplateFieldKind.ExpiryDate, model));
        Assert.AreEqual("Sam Manager", PartyDocumentRenderer.FieldValue(PartyTemplateFieldKind.AuthorisedBy, model));
        Assert.AreEqual("Lily's party", PartyDocumentRenderer.FieldValue(PartyTemplateFieldKind.PartyName, model));
        Assert.AreEqual("Amy Clarke", PartyDocumentRenderer.FieldValue(PartyTemplateFieldKind.PartyHost, model));
        Assert.AreEqual("7", PartyDocumentRenderer.FieldValue(PartyTemplateFieldKind.ChildAge, model));
    }

    [TestMethod]
    public void VoucherExpiry_DefaultsToTwelveMonthsWithoutATemplate()
    {
        PartyDocumentModel model = new(Booking(), null, [], null, "#167a47");

        Assert.AreEqual(new DateOnly(2027, 3, 14), PartyDocumentRenderer.VoucherExpiry(model));
    }
}
