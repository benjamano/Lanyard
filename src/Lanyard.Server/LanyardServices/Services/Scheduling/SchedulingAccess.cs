using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

// The two authorisation questions every scheduling service asks, kept in one place so the
// rule can't drift between positions, PINs, contracts and settings:
//  - may this caller edit company-level configuration for company X?
//  - may this caller change something about user Y?
// Admins may do anything. A Manager acts within the company they logged in under
// (LocationScope.CompanyId); they may touch a user only if that user is a member of a
// location in that company - the user editor loads any user id, so this is what stops a
// manager at one company changing the positions or PIN of someone at another.
internal static class SchedulingAccess
{
    public static bool CanManageCompany(LocationScope scope, int companyId) =>
        scope.IsAdmin || scope.CompanyId == companyId;

    public static async Task<bool> CanManageUserAsync(ApplicationDbContext ctx, LocationScope scope, string userId)
    {
        if (scope.IsAdmin)
        {
            return true;
        }

        if (scope.CompanyId is not int companyId)
        {
            return false;
        }

        return await ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(x => x.UserId == userId && x.Location!.CompanyId == companyId && x.Location.IsActive);
    }
}
