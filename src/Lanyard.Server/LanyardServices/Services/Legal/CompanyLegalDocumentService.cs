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

            var company = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .Where(c => c.Id == companyId && c.IsActive)
                .Select(c => new
                {
                    c.Name,
                    c.LegalName,
                    c.CompanyNumber,
                    c.RegisteredAddress,
                    c.ContactEmail,
                    c.ContactPhone,
                    c.CollectionHoldMinutes,
                    Body = c.LegalDocuments
                        .Where(d => d.DocumentType == type)
                        .Select(d => d.BodyHtml)
                        .FirstOrDefault()
                })
                .FirstOrDefaultAsync();

            if (company is null)
            {
                return Result<string>.Fail("Company not found.");
            }

            string body = company.Body ?? LegalDocumentTemplates.Default(type);

            return Result<string>.Ok(LegalDocumentTemplates.ApplyPlaceholders(
                body,
                company.Name,
                company.LegalName,
                company.CompanyNumber,
                company.RegisteredAddress,
                company.ContactEmail,
                company.ContactPhone,
                company.CollectionHoldMinutes));
        }
        catch (Exception ex)
        {
            return Result<string>.Fail($"Failed to load the document: {ex.Message}");
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
            // Lanyard's wording", so a soft delete would leave the document in a third state
            // that nothing else understands.
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
