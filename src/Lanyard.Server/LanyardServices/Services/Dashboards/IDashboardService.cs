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

    /// <summary>
    /// Records that a user wants the standard home page, ignoring any organisation-wide default.
    /// Passing false clears that choice, putting them back on the organisation default (or their
    /// own dashboard, if they have one).
    /// </summary>
    Task<Result<bool>> SetUseStandardHomePageAsync(string userId, bool useStandardHomePage);

    /// <summary>
    /// Returns the dashboard an Admin or Manager has set as everyone's home screen, or null if
    /// nobody has set one. Like <see cref="GetDefaultDashboardIdAsync"/>, the stored id comes back
    /// without being checked against the dashboard still existing.
    /// </summary>
    Task<Result<Guid?>> GetOrganisationDefaultDashboardIdAsync();

    /// <summary>
    /// Sets the dashboard shown as the home screen for every user who has not chosen their own.
    /// Passing null clears it, returning everyone to the standard home page.
    /// </summary>
    Task<Result<bool>> SetOrganisationDefaultDashboardIdAsync(Guid? dashboardId);

    /// <summary>
    /// Works out which dashboard a user should actually see on the home page: their own choice
    /// first, then the organisation-wide default, then nothing. A user who has explicitly asked
    /// for the standard home page always resolves to null.
    /// </summary>
    Task<Result<HomeScreenDashboardSelection>> GetHomeScreenDashboardAsync(string userId);
}
