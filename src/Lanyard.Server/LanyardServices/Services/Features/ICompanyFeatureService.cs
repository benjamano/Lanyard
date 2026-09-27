using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Features;

public record CompanyFeatureState(CompanyFeatureInfo Info, bool IsEnabled);

public interface ICompanyFeatureService
{
    // Every feature in CompanyFeatureCatalog order, with whether this company has it.
    Task<Result<List<CompanyFeatureState>>> GetFeaturesAsync(int companyId);

    // Admin only.
    Task<Result<bool>> SetFeatureEnabledAsync(LocationScope scope, int companyId, CompanyFeature feature, bool isEnabled, string? updatedByUserId);

    // The features switched off for the signed-in user's company. Always empty for Admins (they
    // run every company, so they keep everything) and for a signed-out visitor (there is no
    // company to check; routes needing a sign-in are refused before this matters). Fails when a
    // non-admin's company can't be worked out, so callers can hide rather than guess.
    Task<Result<IReadOnlySet<CompanyFeature>>> GetDisabledFeaturesForCurrentUserAsync();

    // Convenience over the above for hiding UI: every feature when the check fails, so a paid
    // module is never shown to someone whose company couldn't be confirmed to have it.
    Task<IReadOnlySet<CompanyFeature>> GetHiddenFeaturesForCurrentUserAsync();

    // Convenience over the above: false on failure as well as when switched off.
    Task<bool> IsEnabledForCurrentUserAsync(CompanyFeature feature);
}
