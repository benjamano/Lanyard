using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services;

public interface IDashboardService
{
    Task<Result<IEnumerable<Dashboard>>> GetDashboardsAsync();
    Task<Result<Dashboard>> GetDashboardAsync(Guid dashboardId);
    Task<Result<bool>> DeleteDashboardAsync(Guid dashboardId);
    Task<Result<bool>> CreateDashboardAsync(Dashboard dashboard);
    Task<Result<bool>> SaveDashboardAsync(Dashboard dashboard);
    Task<Result<DashboardWidget>> SaveWidgetAsync(DashboardWidget widget);

    /// <summary>
    /// Returns the dashboard a user has chosen as their home screen, or null if they have not
    /// chosen one. The stored id is returned as-is without checking the dashboard still exists
    /// or is active - that is what lets callers tell "never set" apart from "was set, but the
    /// dashboard has since been deleted", which need different handling on the home page.
    /// </summary>
    Task<Result<Guid?>> GetDefaultDashboardIdAsync(string userId);

    /// <summary>
    /// Sets a user's home screen dashboard. Passing null clears the choice, returning them to
    /// the standard home page.
    /// </summary>
    Task<Result<bool>> SetDefaultDashboardIdAsync(string userId, Guid? dashboardId);
}
