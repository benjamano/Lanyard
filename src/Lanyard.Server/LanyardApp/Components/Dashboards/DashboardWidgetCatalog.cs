using System.Collections.Concurrent;
using System.Reflection;
using Lanyard.App.Components.Dashboards.Designer.Settings;
using Lanyard.App.Components.Dashboards.Widgets;
using Lanyard.Application.Services.Features;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Lanyard.App.Components.Dashboards;

// Groups the designer's widget palette.
public enum WidgetCategory
{
    General,
    VenueAndGames,
    Music,
    KiosksAndProjection,
    Staff,
    Management
}

// Everything the dashboard needs to know about one widget type, in one place: what to call it,
// how to make a new one, which component draws it and which edits its settings in the designer's
// inspector (SettingsComponent, which takes the widget and an OnChanged callback). The grid, the designer's palette and the
// tests all read this, so adding a widget type means one entry here instead of a case in several
// hand-written switches (see the dashboard-widgets skill).
public sealed record WidgetDescriptor(
    WidgetType Type,
    string Name,
    string Description,
    WidgetCategory Category,
    Func<Icon> Icon,
    Func<DashboardWidget> Create,
    Type Component,
    Type? SettingsComponent)
{
    // The paid module this widget belongs to, or null for widgets every company gets.
    public CompanyFeature? Feature => CompanyFeatureCatalog.ForWidget(Type);
}

public static class DashboardWidgetCatalog
{
    public static IReadOnlyList<WidgetDescriptor> All { get; } =
    [
        new(WidgetType.Greeting, "Greeting", "A good morning / afternoon / evening with the viewer's name.",
            WidgetCategory.General, () => new Icons.Regular.Size20.WeatherSunny(), () => new GreetingWidget(), typeof(RenderGreetingWidget), null),
        new(WidgetType.DigitalClock, "Clock", "The time, and optionally the date.",
            WidgetCategory.General, () => new Icons.Regular.Size20.Clock(), () => new DigitalClockWidget(), typeof(RenderDigitalClockWidget), typeof(DigitalClockWidgetSettings)),
        new(WidgetType.TextArea, "Text", "Formatted text: notices, instructions, prices.",
            WidgetCategory.General, () => new Icons.Regular.Size20.TextAddT(), () => new TextAreaWidget(), typeof(RenderTextAreaWidget), typeof(TextAreaWidgetSettings)),
        new(WidgetType.Button, "Button", "Starts, skips or stops a projection program on a kiosk.",
            WidgetCategory.KiosksAndProjection, () => new Icons.Regular.Size20.FlashPlay(), () => new ButtonWidget(), typeof(RenderButtonWidget), typeof(ButtonWidgetSettings)),
        new(WidgetType.ProjectionStatus, "Projection status", "What a kiosk is projecting right now, with optional controls.",
            WidgetCategory.KiosksAndProjection, () => new Icons.Regular.Size20.Live(), () => new ProjectionStatusWidget(), typeof(RenderProjectionStatusWidget), typeof(ProjectionStatusWidgetSettings)),
        new(WidgetType.KioskHealth, "Kiosk health", "Every kiosk and whether it's online, offline first.",
            WidgetCategory.KiosksAndProjection, () => new Icons.Regular.Size20.Desktop(), () => new KioskHealthWidget(), typeof(RenderKioskHealthWidget), typeof(KioskHealthWidgetSettings)),
        new(WidgetType.AutomationRuleStatus, "Automation rule", "Whether an automation rule is on and when it last ran.",
            WidgetCategory.KiosksAndProjection, () => new Icons.Regular.Size20.Flash(), () => new AutomationRuleStatusWidget(), typeof(RenderAutomationRuleStatusWidget), typeof(AutomationRuleStatusWidgetSettings)),
        new(WidgetType.ClientZoneLaserScoreboard, "Laser scoreboard", "The live scores from a laser game kiosk.",
            WidgetCategory.VenueAndGames, () => new Icons.Regular.Size20.Games(), () => new ClientZoneLaserScoreboardWidget(), typeof(RenderClientZoneLaserScoreboardWidget), typeof(LaserScoreboardWidgetSettings)),
        new(WidgetType.ClientZoneLaserGameStatus, "Laser game status", "Whether a game is on and how long is left.",
            WidgetCategory.VenueAndGames, () => new Icons.Regular.Size20.Timer(), () => new ClientZoneLaserGameStatusWidget(), typeof(RenderClientZoneLaserGameStatusWidget), typeof(LaserGameStatusWidgetSettings)),
        new(WidgetType.HallOfFame, "Hall of Fame", "Best scores, accuracy and teams over a period.",
            WidgetCategory.VenueAndGames, () => new Icons.Regular.Size20.Target(), () => new HallOfFameWidget(), typeof(RenderHallOfFameWidget), typeof(HallOfFameWidgetSettings)),
        new(WidgetType.MusicTimeline, "Now playing", "The song a kiosk is playing, with skip buttons and, if you turn them on, volume and EQ.",
            WidgetCategory.Music, () => new Icons.Regular.Size20.MusicNote2Play(), () => new MusicTimelineWidget(), typeof(RenderMusicTimelineWidget), typeof(MusicTimelineWidgetSettings)),
        new(WidgetType.MusicPlaylistSelector, "Playlist picker", "Switch the playlist a kiosk is playing.",
            WidgetCategory.Music, () => new Icons.Regular.Size20.MusicNote2(), () => new MusicPlaylistSelectorWidget(), typeof(RenderMusicPlaylistSelectorWidget), typeof(MusicPlaylistSelectorWidgetSettings)),
        new(WidgetType.Announcements, "Announcements", "The latest staff announcements for the viewer's location.",
            WidgetCategory.Staff, () => new Icons.Regular.Size20.Megaphone(), () => new AnnouncementsWidget(), typeof(RenderAnnouncementsWidget), typeof(AnnouncementsWidgetSettings)),
        new(WidgetType.MyShifts, "My shifts", "The viewer's own upcoming shifts and time off.",
            WidgetCategory.Staff, () => new Icons.Regular.Size20.CalendarPerson(), () => new MyShiftsWidget(), typeof(RenderMyShiftsWidget), typeof(MyShiftsWidgetSettings)),
        new(WidgetType.MyTraining, "My training", "The viewer's own courses and what's due.",
            WidgetCategory.Staff, () => new Icons.Regular.Size20.HatGraduation(), () => new MyTrainingWidget(), typeof(RenderMyTrainingWidget), typeof(MyTrainingWidgetSettings)),
        new(WidgetType.WhoIsOnToday, "Who's on today", "Today's rota at a location, and who has clocked in.",
            WidgetCategory.Staff, () => new Icons.Regular.Size20.PeopleTeam(), () => new WhoIsOnTodayWidget(), typeof(RenderWhoIsOnTodayWidget), typeof(WhoIsOnTodayWidgetSettings)),
        new(WidgetType.ChecklistsToday, "Checklists today", "Today's checklists at a location, with completion progress.",
            WidgetCategory.Management, () => new Icons.Regular.Size20.TaskListLtr(), () => new ChecklistsTodayWidget(), typeof(RenderChecklistsTodayWidget), typeof(ChecklistsTodayWidgetSettings)),
        new(WidgetType.Checklist, "Checklist card", "A chosen checklist with task ticking on a kiosk.",
            WidgetCategory.Management, () => new Icons.Regular.Size20.TaskListLtr(), () => new ChecklistCardWidget(), typeof(RenderChecklistCardWidget), typeof(ChecklistCardWidgetSettings)),
        new(WidgetType.PendingTimeOff, "Time off to approve", "Time-off requests waiting for the viewing manager.",
            WidgetCategory.Management, () => new Icons.Regular.Size20.CalendarClock(), () => new PendingTimeOffWidget(), typeof(RenderPendingTimeOffWidget), typeof(PendingTimeOffWidgetSettings)),
        new(WidgetType.ComplianceSummary, "Compliance at a glance", "Overdue training and expiring staff documents.",
            WidgetCategory.Management, () => new Icons.Regular.Size20.ShieldError(), () => new ComplianceSummaryWidget(), typeof(RenderComplianceSummaryWidget), typeof(ComplianceSummaryWidgetSettings)),
        new(WidgetType.DemandStaffing, "Demand and staffing", "Labour hours against how busy it was.",
            WidgetCategory.Management, () => new Icons.Regular.Size20.DataArea(), () => new DemandStaffingWidget(), typeof(RenderDemandStaffingWidget), typeof(DemandStaffingWidgetSettings)),
        new(WidgetType.WhatsOnToday, "What's on today", "Today's parties and bookings at a location.",
            WidgetCategory.Management, () => new Icons.Regular.Size20.Balloon(), () => new WhatsOnTodayWidget(), typeof(RenderWhatsOnTodayWidget), typeof(WhatsOnTodayWidgetSettings)),
        new(WidgetType.OperationsAlerts, "Operations alerts", "Open kiosk, automation and staff problems.",
            WidgetCategory.Management, () => new Icons.Regular.Size20.Alert(), () => new OperationsAlertsWidget(), typeof(RenderOperationsAlertsWidget), typeof(OperationsAlertsWidgetSettings)),
    ];

    private static readonly Dictionary<WidgetType, WidgetDescriptor> ByType = All.ToDictionary(x => x.Type);

    public static WidgetDescriptor? Get(WidgetType type) => ByType.GetValueOrDefault(type);

    public static string NameOf(WidgetType type) => Get(type)?.Name ?? type.ToString();

    public static string CategoryName(WidgetCategory category) => category switch
    {
        WidgetCategory.General => "General",
        WidgetCategory.VenueAndGames => "Venue & games",
        WidgetCategory.Music => "Music",
        WidgetCategory.KiosksAndProjection => "Kiosks & projection",
        WidgetCategory.Staff => "Staff",
        WidgetCategory.Management => "Management",
        _ => category.ToString()
    };

    // The parameters to hand a widget's renderer. Every renderer takes the widget; the optional
    // ones are only passed to renderers that declare them, since DynamicComponent throws on a
    // parameter the component doesn't have.
    //  - AllowTrigger: buttons and controls only act on a live dashboard, never in the designer.
    //  - PreviewMode:  Text Area shows its content rather than an editor. Always true here: its
    //                  editor lives in the designer's inspector (TextAreaWidgetSettings).
    //  - CompanyId:    a kiosk renders signed out, so company data is scoped to the dashboard's company.
    public static Dictionary<string, object?> RenderParameters(WidgetDescriptor descriptor, DashboardWidget widget, bool live, int? companyId, bool controlsEnabled = true)
    {
        HashSet<string> declared = DeclaredParameters.GetOrAdd(descriptor.Component, ReadParameters);
        Dictionary<string, object?> parameters = new() { ["Widget"] = widget };

        if (declared.Contains("AllowTrigger"))
        {
            parameters["AllowTrigger"] = live && controlsEnabled;
        }

        if (declared.Contains("PreviewMode"))
        {
            parameters["PreviewMode"] = true;
        }

        if (declared.Contains("CompanyId"))
        {
            parameters["CompanyId"] = companyId;
        }

        return parameters;
    }

    private static readonly ConcurrentDictionary<Type, HashSet<string>> DeclaredParameters = new();

    private static HashSet<string> ReadParameters(Type component) =>
        [.. component.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<ParameterAttribute>() is not null)
            .Select(p => p.Name)];
}
