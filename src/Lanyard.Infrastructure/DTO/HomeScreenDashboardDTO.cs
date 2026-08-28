namespace Lanyard.Infrastructure.DTO;

/// <summary>
/// The outcome of working out which dashboard a user should see on the home page, and why.
/// The "why" matters to the caller: a dashboard that has since been deleted needs a different
/// message depending on whether the user chose it themselves or inherited it from the
/// organisation-wide default an admin set.
/// </summary>
public class HomeScreenDashboardSelection
{
    /// <summary>
    /// The dashboard to show, or null for the standard home page.
    /// </summary>
    public Guid? DashboardId { get; set; }

    /// <summary>
    /// True when <see cref="DashboardId"/> came from the organisation-wide default rather than
    /// from the user's own choice. Always false when <see cref="DashboardId"/> is null.
    /// </summary>
    public bool IsOrganisationDefault { get; set; }
}
