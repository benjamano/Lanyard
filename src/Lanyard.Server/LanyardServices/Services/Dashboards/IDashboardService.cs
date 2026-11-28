using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services;

public interface IDashboardService
{
    Task<Result<IEnumerable<Dashboard>>> GetDashboardsAsync();

    /// <summary>
    /// Active dashboards without their widgets, for pickers and lists that only show names.
    /// <see cref="GetDashboardsAsync"/> loads every widget of every dashboard (including text
    /// widgets' full HTML), which nothing that lists dashboards actually needs.
    /// </summary>
    Task<Result<IEnumerable<Dashboard>>> GetDashboardSummariesAsync();
    Task<Result<Dashboard>> GetDashboardAsync(Guid dashboardId);
    Task<Result<bool>> DeleteDashboardAsync(Guid dashboardId);
    /// <summary>
    /// What the dashboards gallery shows: every active dashboard with its widgets' places (for
    /// the layout thumbnail) and the projection programs that show it on a kiosk.
    /// </summary>
    Task<Result<IEnumerable<DashboardCard>>> GetDashboardCardsAsync();

    /// <summary>
    /// Creates a dashboard, with any widgets it already has (a template's), and returns its id.
    /// </summary>
    Task<Result<Guid>> CreateDashboardAsync(Dashboard dashboard);

    /// <summary>
    /// Copies a dashboard and all of its widgets as a new dashboard named "… (copy)", returning
    /// the copy's id. Admins and Managers only.
    /// </summary>
    Task<Result<Guid>> DuplicateDashboardAsync(Guid dashboardId);
    /// <summary>
    /// Saves a dashboard and its widgets. Fails with <see cref="DashboardService.ChangedElsewhereMessage"/>
    /// if the stored dashboard was saved after <paramref name="dashboard"/>'s LastUpdateDate,
    /// unless <paramref name="overwrite"/> is set. On success the dashboard's LastUpdateDate is
    /// updated to the saved time.
    /// </summary>
    Task<Result<bool>> SaveDashboardAsync(Dashboard dashboard, bool overwrite = false);
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
