using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.OperationsAlerts;

// Who is told about a problem at a location: the company's Admins (a member of any of its
// locations), and the Managers of that location who aren't also Admins. A company-wide problem
// (no location) goes to Admins only. Locked accounts are left out. Shared by the kiosk alert
// monitor and OperationsAlertWriter, so the people notified are the people who can see the alert.
public static class OperationsAlertRecipients
{
    public static async Task<(List<string> Admins, List<string> Managers)> GetAsync(
        ApplicationDbContext ctx, int companyId, int? locationId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        List<string> admins = await (
                from membership in ctx.UserLocationMemberships
                join location in ctx.Locations on membership.LocationId equals location.Id
                join userRole in ctx.UserRoles on membership.UserId equals userRole.UserId
                join role in ctx.Roles on userRole.RoleId equals role.Id
                where location.CompanyId == companyId
                    && role.IsActive
                    && role.Name == SchedulingRecipients.AdminRole
                    && membership.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId
                select membership.UserId)
            .AsNoTracking()
            .TagWithCallSite()
            .Distinct()
            .ToListAsync(cancellationToken);

        List<string> managers = locationId is int id
            ? (await SchedulingRecipients.ManagersOfLocationAsync(ctx, id)).Except(admins).ToList()
            : [];

        List<string> everyone = [.. admins, .. managers];

        HashSet<string> locked = [.. await ctx.Users
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => everyone.Contains(x.Id) && x.LockoutEnd != null && x.LockoutEnd > nowUtc)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)];

        return (admins.Where(x => !locked.Contains(x)).ToList(), managers.Where(x => !locked.Contains(x)).ToList());
    }
}
