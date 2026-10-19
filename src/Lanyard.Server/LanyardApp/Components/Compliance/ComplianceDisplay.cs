using Lanyard.Infrastructure.DTO.Compliance;
using Lanyard.Infrastructure.Enum;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.App.Components.Compliance;

// Labels, colours and drill-down links shared by the compliance widget and page, so the two can't
// drift apart. Links only ever go to role-checked pages - never to a file download.
public static class ComplianceDisplay
{
    public const string PagePath = "/manage/compliance";

    public static string Label(ComplianceSectionKind kind) => kind switch
    {
        ComplianceSectionKind.OverdueTraining => "Overdue training",
        ComplianceSectionKind.ExpiredDocuments => "Expired documents",
        ComplianceSectionKind.ExpiringDocuments => "Documents expiring soon",
        ComplianceSectionKind.OverduePolicies => "Overdue policies",
        ComplianceSectionKind.OutstandingPolicies => "Policies to read",
        _ => kind.ToString()
    };

    public static string QueryKey(ComplianceSectionKind kind) => kind switch
    {
        ComplianceSectionKind.OverdueTraining => "overdue-training",
        ComplianceSectionKind.ExpiredDocuments => "expired-documents",
        ComplianceSectionKind.ExpiringDocuments => "expiring-documents",
        ComplianceSectionKind.OverduePolicies => "overdue-policies",
        ComplianceSectionKind.OutstandingPolicies => "outstanding-policies",
        _ => string.Empty
    };

    public static ComplianceSectionKind? FromQueryKey(string? key) => key switch
    {
        "overdue-training" => ComplianceSectionKind.OverdueTraining,
        "expired-documents" => ComplianceSectionKind.ExpiredDocuments,
        "expiring-documents" => ComplianceSectionKind.ExpiringDocuments,
        "overdue-policies" => ComplianceSectionKind.OverduePolicies,
        "outstanding-policies" => ComplianceSectionKind.OutstandingPolicies,
        _ => null
    };

    // The module a section belongs to; a section is hidden when the company doesn't have it.
    public static CompanyFeature Feature(ComplianceSectionKind kind) => kind switch
    {
        ComplianceSectionKind.OverdueTraining => CompanyFeature.Training,
        ComplianceSectionKind.OverduePolicies or ComplianceSectionKind.OutstandingPolicies => CompanyFeature.Policies,
        _ => CompanyFeature.Onboarding
    };

    public static BadgeColor Color(ComplianceSectionKind kind, int count) => count == 0
        ? BadgeColor.Success
        : kind is ComplianceSectionKind.ExpiringDocuments or ComplianceSectionKind.OutstandingPolicies ? BadgeColor.Warning : BadgeColor.Danger;

    public static string PageUrl(ComplianceSectionKind kind, int days, int? locationId)
    {
        string url = $"{PagePath}?section={QueryKey(kind)}&days={days}";

        return locationId is int id ? $"{url}&location={id}" : url;
    }

    public static string PersonUrl(ComplianceSectionKind kind, string userId) => kind switch
    {
        ComplianceSectionKind.OverdueTraining => "/manage/training?tab=by-person",
        ComplianceSectionKind.OverduePolicies or ComplianceSectionKind.OutstandingPolicies => $"/manage/users/{Uri.EscapeDataString(userId)}?tab=policies",
        _ => $"/manage/users/{Uri.EscapeDataString(userId)}?tab=documents"
    };
}
