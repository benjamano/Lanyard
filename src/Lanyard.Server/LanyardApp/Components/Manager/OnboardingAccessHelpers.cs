using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.App.Components.Manager;

// Shared by OnboardingSettings.razor and OnboardingSettingsManager.razor - both need "the
// locations the current (non-Admin) user actually belongs to" as the starting point for their
// own, differently-shaped scoped list (distinct Companies vs. Locations filtered by CompanyId).
internal static class OnboardingAccessHelpers
{
    public static async Task<Result<List<Location>>> GetLocationsForCurrentUserAsync(
        ISecurityService securityService, ICompanyLocationService companyLocationService)
    {
        Result<string> userIdResult = await securityService.GetCurrentUserIdAsync();

        return userIdResult.IsSuccess && userIdResult.Data is not null
            ? await companyLocationService.GetLocationsForUserAsync(userIdResult.Data)
            : Result<List<Location>>.Ok([]);
    }
}
