using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Locations;

/// <summary>
/// The venues the signed-in user is allowed to see.
///
/// <see cref="ICompanyLocationService.GetLocationsAsync"/> with no company id returns every
/// venue on the deployment, which is the right answer for an admin and badly wrong for anyone
/// else: a CanManageKitchen user at one company could otherwise pick another company's venue
/// and edit its menu, reissue its printed QR codes, or read its live tickets.
///
/// Kept as one helper rather than repeated at each call site because there are three of them
/// (kitchen display, kitchen setup, dashboard widget config) and they must not drift.
/// </summary>
public static class VisibleLocations
{
    public static async Task<Result<List<Location>>> GetForCurrentUserAsync(
        ICompanyLocationService companyLocationService,
        ICurrentLocationContext currentLocationContext)
    {
        Result<LocationScope> scope = await currentLocationContext.GetScopeAsync();

        if (!scope.IsSuccess || scope.Data is null)
        {
            return Result<List<Location>>.Fail(scope.Error ?? "Could not determine which venues you can see.");
        }

        if (scope.Data.IsAdmin)
        {
            return await companyLocationService.GetLocationsAsync();
        }

        // Fail closed. A user with no resolvable company gets no venues rather than all of
        // them - the opposite default is how this leaked in the first place.
        if (scope.Data.CompanyId is not int companyId)
        {
            return Result<List<Location>>.Ok([]);
        }

        return await companyLocationService.GetLocationsAsync(companyId);
    }
}
