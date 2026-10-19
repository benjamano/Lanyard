using Lanyard.Infrastructure.DTO.Policies;
using Lanyard.Infrastructure.Models;

namespace Lanyard.App.Components.Policies;

// The editable settings of a policy, shared by the New Policy dialog and the Settings tab.
public class PolicySettingsModel
{
    public const string CompanyWideValue = "company";

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // A location id, or CompanyWideValue. A string because FluentSelect binds strings.
    public string LocationValue { get; set; } = string.Empty;

    public HashSet<Guid> PositionIds { get; set; } = [];

    public int AcknowledgeWithinDays { get; set; } = Policy.DefaultAcknowledgeWithinDays;

    public bool ReacknowledgeOnSchedule { get; set; }
    public int ReacknowledgeEveryMonths { get; set; } = 12;

    public static PolicySettingsModel For(PolicyEditorOptions options) => new()
    {
        LocationValue = options.DefaultLocationId?.ToString() ?? (options.CanChooseCompanyWide ? CompanyWideValue : string.Empty)
    };

    public static PolicySettingsModel From(PolicyDetail detail) => new()
    {
        Name = detail.Name,
        Description = detail.Description,
        LocationValue = detail.LocationId?.ToString() ?? CompanyWideValue,
        PositionIds = [.. detail.PositionIds],
        AcknowledgeWithinDays = detail.AcknowledgeWithinDays,
        ReacknowledgeOnSchedule = detail.ReacknowledgeEveryMonths is not null,
        ReacknowledgeEveryMonths = detail.ReacknowledgeEveryMonths ?? 12
    };

    public PolicySaveRequest ToRequest(Guid? id) => new(
        id,
        Name,
        Description,
        LocationValue == CompanyWideValue ? null : int.TryParse(LocationValue, out int locationId) ? locationId : null,
        [.. PositionIds],
        AcknowledgeWithinDays,
        ReacknowledgeOnSchedule ? ReacknowledgeEveryMonths : null);
}
