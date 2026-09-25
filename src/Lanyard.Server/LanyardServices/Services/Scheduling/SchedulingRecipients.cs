using Lanyard.Infrastructure.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

// Who a scheduling notification goes to when it's "the managers". Matches the people who can act
// on it: SchedulingAccess lets a Manager manage the location they belong to, and an Admin
// anywhere - here an Admin counts only where they're a member, so a head-office Admin isn't
// emailed about every site's requests.
internal static class SchedulingRecipients
{
    public const string AdminRole = "Admin";
    public const string ManagerRole = "Manager";

    public static async Task<List<string>> ManagersOfLocationAsync(ApplicationDbContext ctx, int locationId)
    {
        return await (
                from membership in ctx.UserLocationMemberships
                join userRole in ctx.UserRoles on membership.UserId equals userRole.UserId
                join role in ctx.Roles on userRole.RoleId equals role.Id
                where membership.LocationId == locationId
                    && role.IsActive
                    && (role.Name == AdminRole || role.Name == ManagerRole)
                    && membership.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId
                select membership.UserId)
            .AsNoTracking()
            .TagWithCallSite()
            .Distinct()
            .ToListAsync();
    }

    // Whether the person holds an active Admin or Manager role at all - the same roles and
    // IsActive rule as above, for checks that aren't about one location (chat groups).
    public static Task<bool> IsManagerOrAdminAsync(ApplicationDbContext ctx, string userId) =>
        (from userRole in ctx.UserRoles
         join role in ctx.Roles on userRole.RoleId equals role.Id
         where userRole.UserId == userId && role.IsActive && (role.Name == AdminRole || role.Name == ManagerRole)
         select userRole.UserId)
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync();
}
