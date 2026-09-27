using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.App.Components.Manager;

// "Which companies may the current user configure?" - Admins the company they signed in to (its
// data is all this session can see), a Manager only the companies their own location memberships
// belong to. GetCompaniesAsync() is unconditional and
// would otherwise leak every other company's catalogs. Shared by the Staff Document Types,
// Onboarding and Rota (Positions, Rota Settings) pages so the rule lives in one place.
internal static class ManagerCompanyAccess
{
    public static async Task<List<Company>> GetCompaniesForCurrentUserAsync(
        ISecurityService securityService, ICompanyLocationService companyLocationService)
    {
        bool isAdmin = await securityService.IsCurrentUserInRoleAsync("Admin");

        if (isAdmin)
        {
            Result<List<Company>> result = await companyLocationService.GetSessionCompaniesAsync();
            return result.IsSuccess ? result.Data ?? [] : [];
        }

        Result<List<Location>> locationsResult = await OnboardingAccessHelpers.GetLocationsForCurrentUserAsync(securityService, companyLocationService);

        return locationsResult.IsSuccess && locationsResult.Data is not null
            ? locationsResult.Data.Where(l => l.Company is not null).Select(l => l.Company!).DistinctBy(c => c.Id).ToList()
            : [];
    }
}
