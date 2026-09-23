using Lanyard.Application.Services;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services.Onboarding;

public class OnboardingService(
    IDbContextFactory<ApplicationDbContext> factory,
    IFileService fileService,
    IEmailService emailService,
    ITrainingBrandingResolver brandingResolver,
    IOptions<EmailOptions> emailOptions,
    ILogger<OnboardingService> logger) : IOnboardingService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IFileService _fileService = fileService;
    private readonly IEmailService _emailService = emailService;
    private readonly ITrainingBrandingResolver _brandingResolver = brandingResolver;
    private readonly IOptions<EmailOptions> _emailOptions = emailOptions;
    private readonly ILogger<OnboardingService> _logger = logger;

    // Resend (like most providers) rejects overly large messages once attachments are
    // base64-encoded; dropped rather than failing the whole welcome email, same reasoning as a
    // standing attachment that fails to download below.
    private const long MaxStandingAttachmentBytes = 15 * 1024 * 1024;

    public async Task<Result<CompanyOnboardingSettings?>> GetSettingsAsync(int companyId, int? locationId = null)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CompanyOnboardingSettings? settings = await ctx.CompanyOnboardingSettings
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.LocationId == locationId);

            return Result<CompanyOnboardingSettings?>.Ok(settings);
        }
        catch (Exception ex)
        {
            return Result<CompanyOnboardingSettings?>.Fail($"Failed to retrieve onboarding settings: {ex.Message}");
        }
    }

    public async Task<Result<CompanyOnboardingSettings>> SaveSettingsAsync(CompanyOnboardingSettings settings)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CompanyOnboardingSettings? existing = await ctx.CompanyOnboardingSettings
                .FirstOrDefaultAsync(x => x.CompanyId == settings.CompanyId && x.LocationId == settings.LocationId);

            if (existing is null)
            {
                existing = new CompanyOnboardingSettings
                {
                    Id = Guid.NewGuid(),
                    CompanyId = settings.CompanyId,
                    LocationId = settings.LocationId
                };
                ctx.CompanyOnboardingSettings.Add(existing);
            }

            existing.SendWelcomeEmail = settings.SendWelcomeEmail;
            existing.WelcomeEmailSubject = settings.WelcomeEmailSubject;
            existing.WelcomeEmailBodyHtml = settings.WelcomeEmailBodyHtml;
            existing.AutoAttachStandingDocuments = settings.AutoAttachStandingDocuments;
            existing.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            return Result<CompanyOnboardingSettings>.Ok(existing);
        }
        catch (Exception ex)
        {
            return Result<CompanyOnboardingSettings>.Fail($"Failed to save onboarding settings: {ex.Message}");
        }
    }

    public async Task<Result<List<CompanyOnboardingStandingAttachment>>> GetStandingAttachmentsAsync(int companyId, int? locationId = null)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<CompanyOnboardingStandingAttachment> attachments = await ctx.CompanyOnboardingStandingAttachments
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.FileMetadata)
                .Where(x => x.CompanyId == companyId && x.LocationId == locationId && x.IsActive)
                .OrderBy(x => x.SortOrder)
                .ToListAsync();

            return Result<List<CompanyOnboardingStandingAttachment>>.Ok(attachments);
        }
        catch (Exception ex)
        {
            return Result<List<CompanyOnboardingStandingAttachment>>.Fail($"Failed to retrieve standing attachments: {ex.Message}");
        }
    }

    public async Task<Result<CompanyOnboardingStandingAttachment>> AddStandingAttachmentAsync(int companyId, int? locationId, Guid fileMetadataId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            int nextSortOrder = await ctx.CompanyOnboardingStandingAttachments
                .Where(x => x.CompanyId == companyId && x.LocationId == locationId)
                .CountAsync();

            CompanyOnboardingStandingAttachment attachment = new()
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                LocationId = locationId,
                FileMetadataId = fileMetadataId,
                SortOrder = nextSortOrder,
                IsActive = true
            };

            ctx.CompanyOnboardingStandingAttachments.Add(attachment);
            await ctx.SaveChangesAsync();

            return Result<CompanyOnboardingStandingAttachment>.Ok(attachment);
        }
        catch (Exception ex)
        {
            return Result<CompanyOnboardingStandingAttachment>.Fail($"Failed to add standing attachment: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveStandingAttachmentAsync(Guid attachmentId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CompanyOnboardingStandingAttachment? attachment = await ctx.CompanyOnboardingStandingAttachments
                .FirstOrDefaultAsync(x => x.Id == attachmentId);

            if (attachment is null)
            {
                return Result<bool>.Fail("Standing attachment not found.");
            }

            attachment.IsActive = false;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to remove standing attachment: {ex.Message}");
        }
    }

    public async Task<Result<CompanyOnboardingSettings?>> GetEffectiveSettingsAsync(int companyId, int? locationId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            CompanyOnboardingSettings? settings = await ResolveEffectiveSettingsAsync(ctx, companyId, locationId, cancellationToken);

            return Result<CompanyOnboardingSettings?>.Ok(settings);
        }
        catch (Exception ex)
        {
            return Result<CompanyOnboardingSettings?>.Fail($"Failed to resolve effective onboarding settings: {ex.Message}");
        }
    }

    // Shared by GetEffectiveSettingsAsync (its own short-lived context) and TriggerOnboardingAsync
    // (reuses the context it already has open for the Users lookup) so the latter doesn't pay for
    // a second DB connection on a path that's fired synchronously during user creation.
    private static async Task<CompanyOnboardingSettings?> ResolveEffectiveSettingsAsync(
        ApplicationDbContext ctx, int companyId, int? locationId, CancellationToken cancellationToken)
    {
        // A location-specific override, if one exists and is enabled, replaces the company-wide
        // configuration entirely rather than merging field-by-field - simpler to reason about,
        // and matches how the admin edits one scope at a time in the UI.
        CompanyOnboardingSettings? settings = null;

        if (locationId is int lid)
        {
            settings = await ctx.CompanyOnboardingSettings
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.LocationId == lid, cancellationToken);
        }

        settings ??= await ctx.CompanyOnboardingSettings
            .AsNoTracking()
            .TagWithCallSite()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.LocationId == null, cancellationToken);

        return settings;
    }

    public async Task<Result<bool>> TriggerOnboardingAsync(string userId, int? locationId, CancellationToken cancellationToken,
        string? subjectOverride = null, string? bodyHtmlOverride = null)
    {
        try
        {
            TrainingBranding branding = await _brandingResolver.ResolveAsync(userId, locationId, null);

            if (branding.CompanyId is not int companyId)
            {
                // No resolvable company - nothing to configure onboarding against. Not an error:
                // this can legitimately happen for a user with no location membership yet.
                return Result<bool>.Ok(false);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            UserProfile? user = await ctx.Users.AsNoTracking().TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);

            if (user is null)
            {
                return Result<bool>.Fail("User not found.");
            }

            CompanyOnboardingSettings? settings = await ResolveEffectiveSettingsAsync(ctx, companyId, locationId, cancellationToken);

            if (settings is null || !settings.SendWelcomeEmail)
            {
                return Result<bool>.Ok(false);
            }

            List<EmailAttachment> attachments = [];

            if (settings.AutoAttachStandingDocuments)
            {
                List<CompanyOnboardingStandingAttachment> standingAttachments = await ctx.CompanyOnboardingStandingAttachments
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Include(x => x.FileMetadata)
                    .Where(x => x.CompanyId == companyId && x.LocationId == settings.LocationId && x.IsActive)
                    .OrderBy(x => x.SortOrder)
                    .ToListAsync(cancellationToken);

                // Downloaded in parallel rather than one at a time - this runs synchronously on
                // the CreateUserAsync request path, so a company with several standing
                // attachments shouldn't pay for N sequential storage round trips.
                EmailAttachment?[] downloaded = await Task.WhenAll(standingAttachments
                    .Where(x => x.FileMetadata is not null)
                    .Select(x => DownloadStandingAttachmentAsync(x, companyId, cancellationToken)));

                attachments.AddRange(downloaded.OfType<EmailAttachment>());
            }

            string? logoUrl = branding.LogoFileId is Guid logoFileId
                ? $"{_emailOptions.Value.PublicBaseUrl.TrimEnd('/')}/api/companies/{companyId}/logo?v={logoFileId:N}"
                : null;

            return await _emailService.SendOnboardingWelcomeEmailAsync(
                user,
                subjectOverride ?? settings.WelcomeEmailSubject ?? "Welcome to Lanyard",
                bodyHtmlOverride ?? settings.WelcomeEmailBodyHtml ?? string.Empty,
                logoUrl,
                branding.AccentColorHex,
                attachments);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to trigger onboarding: {ex.Message}");
        }
    }

    private async Task<EmailAttachment?> DownloadStandingAttachmentAsync(
        CompanyOnboardingStandingAttachment standingAttachment, int companyId, CancellationToken cancellationToken)
    {
        FileMetadata fileMetadata = standingAttachment.FileMetadata!;

        if (fileMetadata.FileSize > MaxStandingAttachmentBytes)
        {
            _logger.LogWarning("Standing attachment {FileMetadataId} for company {CompanyId} is {SizeBytes} bytes, over the {MaxBytes}-byte limit - skipped.",
                standingAttachment.FileMetadataId, companyId, fileMetadata.FileSize, MaxStandingAttachmentBytes);
            return null;
        }

        // A standing attachment that can't be loaded is dropped rather than failing the whole
        // welcome email - the new hire still needs to hear from us even if one handbook PDF is
        // temporarily unavailable, same reasoning as CertificateService's logo-loading fallback.
        Result<Stream> downloadResult = await _fileService.DownloadFileAsync(standingAttachment.FileMetadataId, cancellationToken);

        if (!downloadResult.IsSuccess || downloadResult.Data is null)
        {
            _logger.LogWarning("Could not load standing attachment {FileMetadataId} for company {CompanyId}: {Error}",
                standingAttachment.FileMetadataId, companyId, downloadResult.Error);
            return null;
        }

        await using Stream fileStream = downloadResult.Data;
        using MemoryStream buffer = new();
        await fileStream.CopyToAsync(buffer, cancellationToken);

        return new EmailAttachment(fileMetadata.FileName, buffer.ToArray());
    }
}
