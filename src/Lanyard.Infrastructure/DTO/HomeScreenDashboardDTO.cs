namespace Lanyard.Infrastructure.DTO;

/// <summary>
/// The outcome of working out which dashboard a user should see on the home page, and why.
/// The "why" matters to the caller: it decides both whether to explain a fallback and how to
/// word it, since a dashboard the user picked themselves and one an admin set for everyone
/// need different wording.
/// </summary>
public class HomeScreenDashboardSelection
{
    /// <summary>
    /// The dashboard to show, or null for the standard home page. Always an existing, active
    /// dashboard - a stored id pointing at a deleted one resolves past it rather than being
    /// handed to the caller to re-check.
    /// </summary>
    public Guid? DashboardId { get; set; }

    /// <summary>
    /// True when <see cref="DashboardId"/> came from the organisation-wide default rather than
    /// from the user's own choice. Always false when <see cref="DashboardId"/> is null.
    /// </summary>
    public bool IsOrganisationDefault { get; set; }

    /// <summary>
    /// True when the user had chosen their own dashboard but it has since been deleted, so this
    /// selection fell through to the organisation default (or to the standard home page). Lets
    /// the caller tell the user why they are not seeing what they picked.
    /// </summary>
    public bool PersonalDashboardUnavailable { get; set; }
}
