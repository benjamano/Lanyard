using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Onboarding;

public interface IOnboardingService
{
    Task<Result<CompanyOnboardingSettings?>> GetSettingsAsync(int companyId, int? locationId = null);
    Task<Result<CompanyOnboardingSettings>> SaveSettingsAsync(CompanyOnboardingSettings settings);
    Task<Result<List<CompanyOnboardingStandingAttachment>>> GetStandingAttachmentsAsync(int companyId, int? locationId = null);
    Task<Result<CompanyOnboardingStandingAttachment>> AddStandingAttachmentAsync(int companyId, int? locationId, Guid fileMetadataId);
    Task<Result<bool>> RemoveStandingAttachmentAsync(Guid attachmentId);

    /// <summary>
    /// Resolves the settings that would actually apply for a company/location - a
    /// location-specific override if one exists and is enabled, otherwise the company-wide
    /// configuration. Used both by <see cref="TriggerOnboardingAsync"/> and by the register-user
    /// dialog to preview the default welcome email before a per-user override is typed.
    /// </summary>
    Task<Result<CompanyOnboardingSettings?>> GetEffectiveSettingsAsync(int companyId, int? locationId, CancellationToken cancellationToken = default);

    Task<Result<bool>> TriggerOnboardingAsync(string userId, int? locationId, CancellationToken cancellationToken,
        string? subjectOverride = null, string? bodyHtmlOverride = null);
}
