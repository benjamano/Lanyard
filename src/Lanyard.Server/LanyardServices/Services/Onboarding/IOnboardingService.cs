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
    Task<Result<bool>> TriggerOnboardingAsync(string userId, int? locationId, CancellationToken cancellationToken);
}
