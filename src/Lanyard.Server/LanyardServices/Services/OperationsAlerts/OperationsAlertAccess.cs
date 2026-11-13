using Lanyard.Application.Services.Locations;

namespace Lanyard.Application.Services.OperationsAlerts;

// Who may see and act on which alerts, in one place so the page, the widget, the nav badge and the
// live updates can't disagree. Admins see every alert in their company, including company-wide
// ones (no location). A Manager sees the location they signed in to, the same rule as the rota
// and training (SchedulingAccess.CanManageLocation). Nobody else sees any.
//
// The viewer's company comes from the tenant, not the scope: an Admin signed in without a location
// has no company on their LocationScope.
public static class OperationsAlertAccess
{
    public static bool CanUse(LocationScope scope) => scope.IsAdmin || scope.IsManager;

    public static bool CanSee(LocationScope scope, int viewerCompanyId, int alertCompanyId, int? alertLocationId)
    {
        if (viewerCompanyId != alertCompanyId)
        {
            return false;
        }

        if (scope.IsAdmin)
        {
            return true;
        }

        return scope.IsManager && alertLocationId is int id && scope.LocationId == id;
    }
}
