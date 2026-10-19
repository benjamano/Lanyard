using Lanyard.Infrastructure.DTO.Policies;
using Lanyard.Infrastructure.Enum;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.App.Components.Policies;

// Labels, colours and wording shared by the manager pages, My Policies and the home-page card, so
// "overdue by 3 days" means the same thing everywhere. Times are shown in venue (UK) time.
public static class PolicyDisplay
{
    public static string StatusLabel(PolicyStatus status) => status switch
    {
        PolicyStatus.Overdue => "Overdue",
        PolicyStatus.Outstanding => "To read",
        _ => "Up to date"
    };

    public static BadgeColor StatusColor(PolicyStatus status) => status switch
    {
        PolicyStatus.Overdue => BadgeColor.Danger,
        PolicyStatus.Outstanding => BadgeColor.Warning,
        _ => BadgeColor.Success
    };

    // The one line under a person's name (manager view) or a policy's name (staff view).
    public static string Detail(PolicyStatus status, int? acknowledgedVersion, DateTime? acknowledgedAtUtc, DateTime? dueDateUtc, DateTime nowUtc)
    {
        return status switch
        {
            PolicyStatus.Overdue when dueDateUtc is DateTime due => $"Overdue by {Days((nowUtc - due).TotalDays)}",
            PolicyStatus.Outstanding when dueDateUtc is DateTime due => $"Due {Date(due)}",
            _ when acknowledgedVersion is int version && acknowledgedAtUtc is DateTime at => $"Acknowledged version {version} on {Date(at)}",
            _ => "Nothing to do yet"
        };
    }

    public static string Date(DateTime utc) => RotaTime.ToVenueLocal(utc).ToString("d MMM yyyy");

    public static string DateAndTime(DateTime utc) => RotaTime.ToVenueLocal(utc).ToString("d MMM yyyy, HH:mm");

    public static string VersionFileUrl(Guid versionId) => $"api/policies/versions/{versionId}/file";

    public static string AudienceText(string scopeName, IReadOnlyCollection<string> positionNames) =>
        positionNames.Count == 0 ? scopeName : $"{scopeName} · {string.Join(", ", positionNames)}";

    // "Everyone at Ipswich", "Everyone in the company who works as Kitchen or Marshal".
    public static string EveryoneText(bool companyWide, string scopeName, IReadOnlyCollection<string> positionNames)
    {
        string where = companyWide ? "Everyone in the company" : $"Everyone at {scopeName}";
        return positionNames.Count == 0 ? where : $"{where} who works as {string.Join(" or ", positionNames)}";
    }

    private static string Days(double days)
    {
        int whole = Math.Max(1, (int)Math.Ceiling(days));
        return whole == 1 ? "1 day" : $"{whole} days";
    }
}
