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

        Company company = new() { Name = name, IsActive = true };

        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        return company.Id;
    }

    /// <summary>
    /// The defaults are a draft, not a finished document: they contain square-bracket blanks
    /// only the venue can fill. Nothing is shown to a customer until someone has replaced them
    /// and published.
    /// </summary>
    [TestMethod]
    public async Task Published_RefusesUntilTheDocumentIsPublished()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);

        Result<string> result = await GetService(options)
            .GetPublishedAsync(companyId, LegalDocumentType.PrivacyPolicy);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public void EveryDefaultShipsWithABlankToFillIn()
    {
        foreach (LegalDocumentType type in System.Enum.GetValues<LegalDocumentType>())
        {
            Assert.IsTrue(
                LegalDocumentTemplates.HasUnfilledPrompt(LegalDocumentTemplates.Default(type)),
                $"{type}'s default has no prompt, so nothing tells the venue what to fill in.");
        }
    }

    /// <summary>
    /// The whole point of the prompts. Publishing one would tell a paying customer the venue is
    /// called "[your registered trading name]".
    /// </summary>
    [TestMethod]
    public async Task Publish_RefusesWhileABlankRemains()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);
        CompanyLegalDocumentService service = GetService(options);

        await service.SaveAsync(companyId, LegalDocumentType.OrderingTerms,
            LegalDocumentTemplates.Default(LegalDocumentType.OrderingTerms));

        Result<bool> result = await service.SetPublishedAsync(companyId, LegalDocumentType.OrderingTerms, true);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "prompt");
    }

    /// <summary>
    /// Documents written before substitution was removed still contain {{Tokens}}. Nothing fills
    /// them in any more, so publishing one would print "{{ContactEmail}}" on a checkout page.
    /// Caught on the live page during verification, not by a test, which is why there is one now.
    /// </summary>
    [TestMethod]
    public async Task Publish_RefusesADocumentStillHoldingALegacyToken()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);
        CompanyLegalDocumentService service = GetService(options);

        await service.SaveAsync(companyId, LegalDocumentType.RefundPolicy,
            "<h1>Refunds</h1><p>Contact us at {{ContactEmail}}.</p>");

        Result<bool> result = await service.SetPublishedAsync(companyId, LegalDocumentType.RefundPolicy, true);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsFalse((await service.GetPublishedAsync(companyId, LegalDocumentType.RefundPolicy)).IsSuccess);
    }

    [TestMethod]
    public async Task Publish_ThenPublished_ReturnsTheEditedWording()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);
        CompanyLegalDocumentService service = GetService(options);

        await service.SaveAsync(companyId, LegalDocumentType.RefundPolicy,
            "<h1>Refunds</h1><p>We refund within 20 minutes.</p>");

        Assert.IsTrue((await service.SetPublishedAsync(companyId, LegalDocumentType.RefundPolicy, true)).IsSuccess);

        Result<string> published = await service.GetPublishedAsync(companyId, LegalDocumentType.RefundPolicy);

        Assert.IsTrue(published.IsSuccess, published.Error);
        StringAssert.Contains(published.Data, "We refund within 20 minutes.");
    }

    /// <summary>
    /// An edit that puts a blank back must not stay live. Trusting the author to notice is how a
    /// published page quietly starts saying "[your registered address]" again.
    /// </summary>
    [TestMethod]
    public async Task Save_UnpublishesWhenAnEditReintroducesABlank()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);
        CompanyLegalDocumentService service = GetService(options);

        await service.SaveAsync(companyId, LegalDocumentType.PrivacyPolicy, "<p>All filled in.</p>");
        await service.SetPublishedAsync(companyId, LegalDocumentType.PrivacyPolicy, true);

        await service.SaveAsync(companyId, LegalDocumentType.PrivacyPolicy, "<p>Operated by [your name].</p>");

        Result<DocumentState> state = await service.GetStateAsync(companyId, LegalDocumentType.PrivacyPolicy);

        Assert.IsFalse(state.Data!.IsPublished);
        Assert.IsFalse((await service.GetPublishedAsync(companyId, LegalDocumentType.PrivacyPolicy)).IsSuccess);
    }

    [TestMethod]
    public async Task Publish_RefusesForADocumentThatWasNeverSaved()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);

        Result<bool> result = await GetService(options)
            .SetPublishedAsync(companyId, LegalDocumentType.OrderingTerms, true);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task AreAllDocumentsPublished_OnlyWhenEveryOneIs()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);
        CompanyLegalDocumentService service = GetService(options);

        LegalDocumentType[] all = System.Enum.GetValues<LegalDocumentType>();

        foreach (LegalDocumentType type in all)
        {
            Assert.IsFalse((await service.AreAllDocumentsPublishedAsync(companyId)).Data,
                "Reported ready before every document was published.");

            await service.SaveAsync(companyId, type, $"<p>{type}, fully written out.</p>");
            await service.SetPublishedAsync(companyId, type, true);
        }

        Assert.IsTrue((await service.AreAllDocumentsPublishedAsync(companyId)).Data);
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

        await GetService(options).SaveAsync(companyId, LegalDocumentType.PrivacyPolicy,
            "<p>Hello</p><script>alert('xss')</script><img src=x onerror=\"alert(1)\">");

        await using ApplicationDbContext ctx = new(options);
        string stored = (await ctx.CompanyLegalDocuments.SingleAsync()).BodyHtml;

        Assert.IsFalse(stored.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(stored.Contains("onerror", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(stored, "Hello");
    }

    /// <summary>
    /// Clearing the box is refused rather than treated as a reset: silently republishing
    /// Lanyard's draft would put back text the author had just deleted on purpose.
    /// </summary>
    [TestMethod]
    public async Task Save_RefusesAnEmptyDocument()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);

        Assert.IsFalse((await GetService(options)
            .SaveAsync(companyId, LegalDocumentType.OrderingTerms, "   ")).IsSuccess);
    }

    /// <summary>
    /// Resetting drops the company's copy, which also takes it off the customer site - the draft
    /// it falls back to contains blanks and must not be shown.
    /// </summary>
    [TestMethod]
    public async Task Reset_RestoresTheDraftAndStopsPublishing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);
        CompanyLegalDocumentService service = GetService(options);

        await service.SaveAsync(companyId, LegalDocumentType.PrivacyPolicy, "<p>Our own words.</p>");
        await service.SetPublishedAsync(companyId, LegalDocumentType.PrivacyPolicy, true);

        Assert.IsTrue((await service.ResetToDefaultAsync(companyId, LegalDocumentType.PrivacyPolicy)).IsSuccess);

        Result<DocumentState> state = await service.GetStateAsync(companyId, LegalDocumentType.PrivacyPolicy);
        Assert.IsFalse(state.Data!.IsCustomised);
        Assert.IsFalse(state.Data.IsPublished);

        StringAssert.Contains(
            (await service.GetForEditingAsync(companyId, LegalDocumentType.PrivacyPolicy)).Data, "data controller");
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
}
