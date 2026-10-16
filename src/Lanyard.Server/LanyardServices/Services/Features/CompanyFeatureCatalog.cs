using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Features;

public record CompanyFeatureInfo(CompanyFeature Feature, string Name, string Description);

// Display text for each switch on the Companies & Locations page, and the name used in the
// "not available" message a blocked page shows. Every CompanyFeature member must be listed here
// (CompanyFeatureCatalogTests checks it), or it gets no switch and can't be turned off.
public static class CompanyFeatureCatalog
{
    public static IReadOnlyList<CompanyFeatureInfo> All { get; } =
    [
        new(CompanyFeature.StaffScheduling, "Shift management",
            "Rota builder, My Shifts, open shifts and swaps, time off, timesheets and clock-in terminals."),
        new(CompanyFeature.Training, "Training",
            "Courses, My Training, the training dashboard and the My Training widget."),
        new(CompanyFeature.Chat, "Chat",
            "Staff chat, channels and chat moderation."),
        new(CompanyFeature.Onboarding, "Onboarding",
            "Onboarding emails and staff document types."),
        new(CompanyFeature.Announcements, "Announcements",
            "The Announcements page and widget."),
        new(CompanyFeature.Insights, "Insights",
            "Busy hours against staffing: games, players, clocked and rota hours by hour of the week.")
    ];

    // The module a dashboard widget belongs to, or null for widgets every company gets.
    // ComplianceSummary is deliberately null: it spans Training and Onboarding, so it hides each of
    // its sections itself rather than the whole widget disappearing for want of one module.
    public static CompanyFeature? ForWidget(WidgetType type) => type switch
    {
        WidgetType.MyTraining => CompanyFeature.Training,
        WidgetType.MyShifts or WidgetType.WhoIsOnToday or WidgetType.PendingTimeOff => CompanyFeature.StaffScheduling,
        WidgetType.Announcements => CompanyFeature.Announcements,
        _ => null
    };

    public static CompanyFeatureInfo Get(CompanyFeature feature) =>
        All.FirstOrDefault(x => x.Feature == feature) ?? new(feature, feature.ToString(), string.Empty);
}
