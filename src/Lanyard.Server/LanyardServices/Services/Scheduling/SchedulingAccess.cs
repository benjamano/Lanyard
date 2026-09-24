using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

// The authorisation questions every scheduling service asks, kept in one place so the
// rule can't drift between positions, PINs, contracts and settings:
//  - may this caller edit company-level configuration for company X?
//  - may this caller change something about user Y?
// Admins may do anything. A Manager acts within the company they logged in under
// (LocationScope.CompanyId); they may touch a user only if that user is a member of a
// location in that company - the user editor loads any user id, so this is what stops a
// manager at one company changing the positions or PIN of someone at another. Anyone who is
// neither is refused outright: the pages are role-gated too, but the service is the boundary
// that must hold if something else ever calls it.
internal static class SchedulingAccess
{
    public static bool CanManageCompany(LocationScope scope, int companyId) =>
        scope.IsAdmin || (scope.IsManager && scope.CompanyId == companyId);

    // The rota is per location, and a Manager builds the rota for the location they logged in
    // under - the same rule CourseService applies to training (see the location-scoping skill).
    public static bool CanManageLocation(LocationScope scope, int locationId) =>
        scope.IsAdmin || (scope.IsManager && scope.LocationId == locationId);

    public static async Task<bool> CanManageUserAsync(ApplicationDbContext ctx, LocationScope scope, string userId)
    {
        if (scope.IsAdmin)
        {
            return true;
        }

        if (!scope.IsManager || scope.CompanyId is not int companyId)
        {
            return false;
        }

        return await ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(x => x.UserId == userId && x.Location!.CompanyId == companyId && x.Location.IsActive);
    }
}
