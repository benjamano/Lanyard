using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.Branding;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Training;

/// <summary>
/// The branding (accent colour + logo) to stamp on a training email or certificate.
/// </summary>
public record TrainingBranding(string AccentColorHex, int? CompanyId, Guid? LogoFileId)
{
    public static TrainingBranding Default { get; } = new(BrandConstants.PrimaryColorHex, null, null);
}

public interface ITrainingBrandingResolver
{
    Task<TrainingBranding> ResolveAsync(string userId, int? assignmentLocationId, int? courseLocationId);
}

/// <summary>
/// Works out whose branding a learner's training email or certificate should carry.
/// </summary>
/// <remarks>
/// This used to read <c>CourseAssignment.LocationId</c> directly, which is wrong: that field
/// records the location of whoever *assigned* the course (and is nullable), not the learner
/// the document belongs to. The visible symptom was two people at the same location getting
/// differently-branded certificates purely because their assignments were created by
/// different routes - an admin bulk assign stamps the course's location, a manager's assign
/// stamps the manager's own, and auto-assign-on-user-creation can leave it null entirely.
///
/// The learner's own company is asked first because that is what the document is actually
/// about; assignment and course locations remain as fallbacks so a learner with no
/// membership (or one spanning two companies) still gets something sensible.
/// </remarks>
public class TrainingBrandingResolver(
    ICompanyLocationService companyLocationService,
    ILogger<TrainingBrandingResolver> logger) : ITrainingBrandingResolver
{
    private readonly ICompanyLocationService _companyLocationService = companyLocationService;
    private readonly ILogger<TrainingBrandingResolver> _logger = logger;

    public async Task<TrainingBranding> ResolveAsync(string userId, int? assignmentLocationId, int? courseLocationId)
    {
        try
        {
            Result<CompanyBrandingInfo> byUser = await _companyLocationService.GetCompanyBrandingForUserAsync(userId);

            if (byUser.IsSuccess && byUser.Data is not null)
            {
                return ToBranding(byUser.Data);
            }

            foreach (int locationId in new[] { assignmentLocationId, courseLocationId }.OfType<int>())
            {
                Result<CompanyBrandingInfo> byLocation = await _companyLocationService.GetCompanyBrandingForLocationAsync(locationId);

                if (byLocation.IsSuccess && byLocation.Data is not null)
                {
                    return ToBranding(byLocation.Data);
                }
            }

            _logger.LogWarning(
                "No company branding resolved for user {UserId} (assignment location {AssignmentLocationId}, course location {CourseLocationId}); using default Lanyard branding.",
                userId, assignmentLocationId, courseLocationId);

            return TrainingBranding.Default;
        }
        catch (Exception ex)
        {
            // Branding is decoration - never fail the email or certificate over it.
            _logger.LogWarning(ex, "Failed to resolve training branding for user {UserId}; using default Lanyard branding.", userId);

            return TrainingBranding.Default;
        }
    }

    private static TrainingBranding ToBranding(CompanyBrandingInfo info) =>
        new(BrandConstants.ResolveAccentColor(info.ThemeColorHex), info.CompanyId, info.LogoFileId);
}
