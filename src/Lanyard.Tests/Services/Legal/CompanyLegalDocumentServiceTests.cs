using Lanyard.Application.Services.Legal;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Lanyard.Tests.Services.Locations;
using Moq;

namespace Lanyard.Tests.Services.Legal;

/// <summary>
/// Company-editable ordering terms, refund policy and privacy policy.
///
/// The two things that matter here are that a company which has never edited still publishes a
/// complete document, and that what a company types cannot become script on a customer's
/// checkout page.
/// </summary>
[TestClass]
public class CompanyLegalDocumentServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static CompanyLegalDocumentService GetService(
        DbContextOptions<ApplicationDbContext> options, ICompanyAccessService? access = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new CompanyLegalDocumentService(
            factoryMock.Object,
            access ?? CompanyAccessMocks.Admin(),
            new Mock<ILogger<CompanyLegalDocumentService>>().Object);
    }

    private static async Task<int> SeedCompanyAsync(
        DbContextOptions<ApplicationDbContext> options, string name = "Play2Day")
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new()
        {
            Name = name,
            IsActive = true,
            LegalName = $"{name} Leisure Ltd",
            CompanyNumber = "09876543",
            RegisteredAddress = "1 Cardinal Park, Ipswich, IP1 1AA",
            ContactEmail = "hello@example.test",
            ContactPhone = "01473 000000",
            CollectionHoldMinutes = 20
        };

        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        return company.Id;
    }

    /// <summary>
    /// Nothing was backfilled when documents became editable, so a company with no row has to
    /// fall through to Lanyard's wording rather than publishing a blank page.
    /// </summary>
    [TestMethod]
    public async Task Published_FallsBackToTheDefaultWordingWhenNothingHasBeenEdited()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);

        Result<string> result = await GetService(options)
            .GetPublishedAsync(companyId, LegalDocumentType.PrivacyPolicy);

        Assert.IsTrue(result.IsSuccess, result.Error);
        StringAssert.Contains(result.Data, "Privacy policy");
    }

    [TestMethod]
    public async Task Published_SubstitutesTheCompanysOwnDetails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);

        Result<string> result = await GetService(options)
            .GetPublishedAsync(companyId, LegalDocumentType.OrderingTerms);

        Assert.IsTrue(result.IsSuccess, result.Error);
        StringAssert.Contains(result.Data, "Play2Day Leisure Ltd");
        StringAssert.Contains(result.Data, "1 Cardinal Park, Ipswich, IP1 1AA");
        StringAssert.Contains(result.Data, "20 minutes");

        // No token should survive into what a customer reads.
        Assert.IsFalse(LegalDocumentTemplates.HasUnknownPlaceholder(result.Data!),
            "A placeholder was left unsubstituted in the published document.");
    }

    /// <summary>
    /// One company's document must never be able to display another's identity, which is why
    /// substitution happens server-side against the company that owns the document.
    /// </summary>
    [TestMethod]
    public async Task Published_UsesEachCompanysOwnDetailsForTheSameTemplate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int play2Day = await SeedCompanyAsync(options, "Play2Day");
        int partyman = await SeedCompanyAsync(options, "Partyman");

        CompanyLegalDocumentService service = GetService(options);

        Result<string> a = await service.GetPublishedAsync(play2Day, LegalDocumentType.OrderingTerms);
        Result<string> b = await service.GetPublishedAsync(partyman, LegalDocumentType.OrderingTerms);

        StringAssert.Contains(a.Data, "Play2Day Leisure Ltd");
        Assert.IsFalse(a.Data!.Contains("Partyman"));
        StringAssert.Contains(b.Data, "Partyman Leisure Ltd");
        Assert.IsFalse(b.Data!.Contains("Play2Day"));
    }

    [TestMethod]
    public async Task Save_ThenPublished_ReturnsTheEditedWording()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);
        CompanyLegalDocumentService service = GetService(options);

        Result<bool> saved = await service.SaveAsync(
            companyId, LegalDocumentType.RefundPolicy, "<h1>Refunds</h1><p>We refund within {{CollectionHoldMinutes}} minutes.</p>");

        Assert.IsTrue(saved.IsSuccess, saved.Error);

        Result<string> published = await service.GetPublishedAsync(companyId, LegalDocumentType.RefundPolicy);

        StringAssert.Contains(published.Data, "We refund within 20 minutes.");
    }

    /// <summary>
    /// This is staff-authored HTML rendered unescaped on a public page. A compromised staff
    /// account must not be able to put script on a customer's checkout, so the stored value is
    /// the sanitised one rather than the sanitising being left to whoever renders it.
    /// </summary>
    [TestMethod]
    public async Task Save_StripsScriptBeforeItIsEverStored()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);
        CompanyLegalDocumentService service = GetService(options);

        await service.SaveAsync(companyId, LegalDocumentType.PrivacyPolicy,
            "<p>Hello</p><script>alert('xss')</script><img src=x onerror=\"alert(1)\">");

        await using ApplicationDbContext ctx = new(options);
        string stored = (await ctx.CompanyLegalDocuments.SingleAsync()).BodyHtml;

        Assert.IsFalse(stored.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(stored.Contains("onerror", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(stored, "Hello");
    }

    /// <summary>
    /// Clearing the box is refused rather than treated as a reset: silently republishing
    /// Lanyard's wording would put back text the author had just deleted on purpose.
    /// </summary>
    [TestMethod]
    public async Task Save_RefusesAnEmptyDocument()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);

        Result<bool> result = await GetService(options)
            .SaveAsync(companyId, LegalDocumentType.OrderingTerms, "   ");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Reset_RestoresTheDefaultWording()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);
        CompanyLegalDocumentService service = GetService(options);

        await service.SaveAsync(companyId, LegalDocumentType.PrivacyPolicy, "<p>Our own words.</p>");
        Assert.IsTrue((await service.IsCustomisedAsync(companyId, LegalDocumentType.PrivacyPolicy)).Data);

        Result<bool> reset = await service.ResetToDefaultAsync(companyId, LegalDocumentType.PrivacyPolicy);

        Assert.IsTrue(reset.IsSuccess, reset.Error);
        Assert.IsFalse((await service.IsCustomisedAsync(companyId, LegalDocumentType.PrivacyPolicy)).Data);

        Result<string> published = await service.GetPublishedAsync(companyId, LegalDocumentType.PrivacyPolicy);
        Assert.IsFalse(published.Data!.Contains("Our own words."));
        StringAssert.Contains(published.Data, "data controller");
    }

    [TestMethod]
    public async Task Save_UpdatesInPlaceRatherThanAddingASecondDocument()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);
        CompanyLegalDocumentService service = GetService(options);

        await service.SaveAsync(companyId, LegalDocumentType.OrderingTerms, "<p>First.</p>");
        await service.SaveAsync(companyId, LegalDocumentType.OrderingTerms, "<p>Second.</p>");

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.CompanyLegalDocuments.CountAsync());
        StringAssert.Contains((await ctx.CompanyLegalDocuments.SingleAsync()).BodyHtml, "Second.");
    }

    /// <summary>
    /// Every default has to be publishable as shipped - a token nobody substitutes would appear
    /// verbatim on a customer's screen.
    /// </summary>
    [TestMethod]
    public async Task EveryDefaultDocumentPublishesWithNoLeftoverPlaceholders()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);
        CompanyLegalDocumentService service = GetService(options);

        foreach (LegalDocumentType type in Enum.GetValues<LegalDocumentType>())
        {
            Result<string> published = await service.GetPublishedAsync(companyId, type);

            Assert.IsTrue(published.IsSuccess, $"{type}: {published.Error}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(published.Data), $"{type} published as empty.");
            Assert.IsFalse(LegalDocumentTemplates.HasUnknownPlaceholder(published.Data!),
                $"{type} still contains an unsubstituted placeholder.");
        }
    }
}
