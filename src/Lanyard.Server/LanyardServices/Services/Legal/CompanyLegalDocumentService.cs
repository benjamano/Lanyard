using Ganss.Xss;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Legal;

/// <summary>Whether a company has saved its own copy of a document, and whether it is live.</summary>
public record DocumentState(bool IsCustomised, bool IsPublished);

public interface ICompanyLegalDocumentService
{
    /// <summary>
    /// The document as an author should see it: their edited copy if there is one, otherwise
    /// Lanyard's default wording, with placeholders left intact so they can be edited around.
    /// </summary>
    Task<Result<string>> GetForEditingAsync(int companyId, LegalDocumentType type);

    /// <summary>
    /// The document as a customer should see it: placeholders replaced with this company's own
    /// details. This is the only method the public ordering site calls.
    /// </summary>
    Task<Result<string>> GetPublishedAsync(int companyId, LegalDocumentType type);

    Task<Result<bool>> SaveAsync(int companyId, LegalDocumentType type, string bodyHtml);

    /// <summary>Whether a company has published every document a customer must be shown.</summary>
    Task<Result<bool>> AreAllDocumentsPublishedAsync(int companyId);

    Task<Result<DocumentState>> GetStateAsync(int companyId, LegalDocumentType type);

    Task<Result<bool>> SetPublishedAsync(int companyId, LegalDocumentType type, bool isPublished);

    /// <summary>Discards the company's copy so the document falls back to Lanyard's wording.</summary>
    Task<Result<bool>> ResetToDefaultAsync(int companyId, LegalDocumentType type);

    /// <summary>Whether this company has edited the document, for showing "customised" in the UI.</summary>
    Task<Result<bool>> IsCustomisedAsync(int companyId, LegalDocumentType type);
}

/// <summary>
/// Customer-facing legal documents, editable per company.
///
/// These used to be hardcoded in Razor markup, which meant a wording change was a code change and
/// every tenant necessarily said the same thing. They are now rows, with Lanyard's wording as the
/// fallback rather than as a seeded copy - so a company that never opens the editor still
/// publishes a complete document, and still picks up improvements to the default wording.
/// </summary>
public class CompanyLegalDocumentService(
    IDbContextFactory<ApplicationDbContext> factory,
    ICompanyAccessService companyAccess,
    ILogger<CompanyLegalDocumentService> logger) : ICompanyLegalDocumentService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ICompanyAccessService _companyAccess = companyAccess;
    private readonly ILogger<CompanyLegalDocumentService> _logger = logger;

    /// <summary>
    /// Static because HtmlSanitizer is thread-safe once configured and building one per save is
    /// pure overhead. Defaults allow the formatting a rich-text editor produces and nothing that
    /// executes: no script, no iframes, no event handlers, no javascript: URLs.
    /// </summary>
    private static readonly HtmlSanitizer _sanitizer = new();

    private const int MaxBodyLength = 60_000;

    public async Task<Result<string>> GetForEditingAsync(int companyId, LegalDocumentType type)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? stored = await ctx.CompanyLegalDocuments
                .AsNoTracking()
                .TagWithCallSite()
                .Where(d => d.CompanyId == companyId && d.DocumentType == type)
                .Select(d => d.BodyHtml)
                .FirstOrDefaultAsync();

            return Result<string>.Ok(stored ?? LegalDocumentTemplates.Default(type));
        }
        catch (Exception ex)
        {
            return Result<string>.Fail($"Failed to load the document: {ex.Message}");
        }
    }

    public async Task<Result<string>> GetPublishedAsync(int companyId, LegalDocumentType type)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            var document = await ctx.CompanyLegalDocuments
                .AsNoTracking()
                .TagWithCallSite()
                .Where(d => d.CompanyId == companyId && d.DocumentType == type)
                .Select(d => new { d.BodyHtml, d.IsPublished })
                .FirstOrDefaultAsync();

            // Unpublished, or never written, is withheld rather than shown. The defaults are a
            // draft containing "[your registered address]", and putting that in front of a
            // customer is worse than telling them plainly it is not ready.
            if (document is null || !document.IsPublished)
            {
                return Result<string>.Fail("This document has not been published yet.");
            }

            return Result<string>.Ok(document.BodyHtml);
        }
        catch (Exception ex)
        {
            return Result<string>.Fail($"Failed to load the document: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether this company has published everything a customer has to be shown before buying.
    /// All three, because all three are linked from the ordering flow.
    /// </summary>
    public async Task<Result<bool>> AreAllDocumentsPublishedAsync(int companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            int published = await ctx.CompanyLegalDocuments
                .AsNoTracking()
                .TagWithCallSite()
                .CountAsync(d => d.CompanyId == companyId && d.IsPublished);

            return Result<bool>.Ok(published >= Enum.GetValues<LegalDocumentType>().Length);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to check the company's documents: {ex.Message}");
        }
    }

    public async Task<Result<DocumentState>> GetStateAsync(int companyId, LegalDocumentType type)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            var document = await ctx.CompanyLegalDocuments
                .AsNoTracking()
                .TagWithCallSite()
                .Where(d => d.CompanyId == companyId && d.DocumentType == type)
                .Select(d => new { d.IsPublished })
                .FirstOrDefaultAsync();

            return Result<DocumentState>.Ok(new DocumentState(document is not null, document?.IsPublished ?? false));
        }
        catch (Exception ex)
        {
            return Result<DocumentState>.Fail($"Failed to check the document: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetPublishedAsync(int companyId, LegalDocumentType type, bool isPublished)
    {
        try
        {
            if (!await _companyAccess.CanAdministerCompanyAsync(companyId))
            {
                return Result<bool>.Fail("You don't have permission to edit this company's documents.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CompanyLegalDocument? document = await ctx.CompanyLegalDocuments
                .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.DocumentType == type);

            if (document is null)
            {
                return Result<bool>.Fail("Save the document before publishing it.");
            }

            if (isPublished && LegalDocumentTemplates.HasUnfilledPrompt(document.BodyHtml))
            {
                // Refused rather than warned: the prompts are square-bracket blanks like
                // "[your registered address]", and publishing one puts that text in front of a
                // paying customer as the venue's own legal identity.
                return Result<bool>.Fail(
                    "This document still contains a [prompt] to fill in. Replace it before publishing.");
            }

            document.IsPublished = isPublished;
            document.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Company {CompanyId} set {DocumentType} published to {IsPublished}",
                companyId, type, isPublished);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to update the document: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SaveAsync(int companyId, LegalDocumentType type, string bodyHtml)
    {
        try
        {
            if (!await _companyAccess.CanAdministerCompanyAsync(companyId))
            {
                return Result<bool>.Fail("You don't have permission to edit this company's documents.");
            }

            if (string.IsNullOrWhiteSpace(bodyHtml))
            {
                // Refused rather than treated as "reset": silently falling back to Lanyard's
                // wording when someone clears the box would publish text they had just deleted.
                return Result<bool>.Fail("The document can't be empty. Use \"Reset to default\" if you want Lanyard's wording back.");
            }

            if (bodyHtml.Length > MaxBodyLength)
            {
                return Result<bool>.Fail($"That document is too long (limit {MaxBodyLength:N0} characters).");
            }

            // Sanitised here, on the way in, so that the stored value is the safe one. This is
            // rendered unescaped on a public page, and the store is the single choke point every
            // edit passes through.
            string clean = _sanitizer.Sanitize(bodyHtml);

            if (string.IsNullOrWhiteSpace(clean))
            {
                return Result<bool>.Fail("That document didn't contain anything we could publish.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (!await ctx.Companies.AnyAsync(c => c.Id == companyId))
            {
                return Result<bool>.Fail("Company not found.");
            }

            DateTime now = DateTime.UtcNow;

            CompanyLegalDocument? existing = await ctx.CompanyLegalDocuments
                .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.DocumentType == type);

            if (existing is null)
            {
                await ctx.CompanyLegalDocuments.AddAsync(new CompanyLegalDocument
                {
                    CompanyId = companyId,
                    DocumentType = type,
                    BodyHtml = clean,
                    CreateDate = now,
                    UpdateDate = now
                });
            }
            else
            {
                existing.BodyHtml = clean;
                existing.UpdateDate = now;

                // An edit that reintroduces a blank must not stay live. Unpublishing is safer
                // than trusting the author to notice, and republishing is one click.
                if (existing.IsPublished && LegalDocumentTemplates.HasUnfilledPrompt(clean))
                {
                    existing.IsPublished = false;
                }
            }

            await ctx.SaveChangesAsync();

            // Logged because it is a change to what customers are told they have agreed to, and
            // "when did the terms last change" is a question that gets asked after a dispute.
            _logger.LogInformation("Company {CompanyId} updated its {DocumentType}", companyId, type);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save {DocumentType} for company {CompanyId}", type, companyId);

            return Result<bool>.Fail($"Failed to save the document: {ex.Message}");
        }
    }

    public async Task<Result<bool>> ResetToDefaultAsync(int companyId, LegalDocumentType type)
    {
        try
        {
            if (!await _companyAccess.CanAdministerCompanyAsync(companyId))
            {
                return Result<bool>.Fail("You don't have permission to edit this company's documents.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CompanyLegalDocument? existing = await ctx.CompanyLegalDocuments
                .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.DocumentType == type);

            if (existing is null)
            {
                // Already on the default wording, so there is nothing to undo.
                return Result<bool>.Ok(true);
            }

            // Genuinely removed rather than flagged inactive: "no row" is what means "use
            // Lanyard's draft", so a soft delete would leave the document in a third state that
            // nothing else understands. Removing the row also unpublishes it, which is correct -
            // the draft contains [blanks] and must not go back in front of customers.
            ctx.CompanyLegalDocuments.Remove(existing);
            await ctx.SaveChangesAsync();

            _logger.LogInformation("Company {CompanyId} reset its {DocumentType} to the default wording",
                companyId, type);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to reset the document: {ex.Message}");
        }
    }

    public async Task<Result<bool>> IsCustomisedAsync(int companyId, LegalDocumentType type)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            return Result<bool>.Ok(await ctx.CompanyLegalDocuments
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(d => d.CompanyId == companyId && d.DocumentType == type));
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to check the document: {ex.Message}");
        }
    }
}
