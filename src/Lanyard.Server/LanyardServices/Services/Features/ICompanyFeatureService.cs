using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Features;

public record CompanyFeatureState(CompanyFeatureInfo Info, bool IsEnabled);

public record FeatureLocation(int LocationId, string Name);

// The switches on the Companies & Locations page: every catalog feature against every active
// location of one company.
public record CompanyFeatureGrid(
    IReadOnlyList<CompanyFeatureInfo> Features,
    IReadOnlyList<FeatureLocation> Locations,
    HashSet<(int LocationId, CompanyFeature Feature)> Enabled)
{
    public bool IsEnabled(int locationId, CompanyFeature feature) => Enabled.Contains((locationId, feature));
}

// Features are switched on per location. No switch means off.
public interface ICompanyFeatureService
{
    // Every feature in CompanyFeatureCatalog order, with whether any active location of the
    // company has it.
    Task<Result<List<CompanyFeatureState>>> GetFeaturesAsync(int companyId);

    // Every feature against every active location of the company.
    Task<Result<CompanyFeatureGrid>> GetFeatureGridAsync(int companyId);

    // Platform admins only.
    Task<Result<bool>> SetFeatureEnabledAsync(LocationScope scope, int locationId, CompanyFeature feature, bool isEnabled, string? updatedByUserId);

    // The features switched off at the signed-in user's current location. Always empty for
    // Admins (they run every company, so they keep everything) and for a signed-out visitor
    // (there is no location to check; routes needing a sign-in are refused before this matters).
    // Fails when a non-admin's location can't be worked out, so callers can hide rather than guess.
    Task<Result<IReadOnlySet<CompanyFeature>>> GetDisabledFeaturesForCurrentUserAsync();

    // Convenience over the above for hiding UI: every feature when the check fails, so a paid
    // module is never shown to someone whose location couldn't be confirmed to have it.
    Task<IReadOnlySet<CompanyFeature>> GetHiddenFeaturesForCurrentUserAsync();

    // Convenience over the above: false on failure as well as when switched off.
    Task<bool> IsEnabledForCurrentUserAsync(CompanyFeature feature);

    // Whether any active location of the company has the feature, ignoring who's asking. For
    // company-wide work, and where Admins (who always see every feature) shouldn't be offered
    // what the server will refuse. False when the check fails.
    Task<bool> IsEnabledForCompanyAsync(int companyId, CompanyFeature feature);

    // Whether one location has the feature, ignoring who's asking. False when the check fails.
    Task<bool> IsEnabledForLocationAsync(int locationId, CompanyFeature feature);
}
