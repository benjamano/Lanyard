using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Lanyard.Infrastructure.DTO.Compliance;
using Lanyard.Infrastructure.DTO.Insights;
using Lanyard.Infrastructure.Enum;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.Infrastructure.Models;

public class Dashboard : ICompanyOwned
{
    public Guid Id { get; set; }
    public int CompanyId { get; set; }

    [Required]
    [StringLength(100, ErrorMessage = "The Name field can not be longer than 100 characters.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "The Description field can not be longer than 500 characters.")]
    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreateDate { get; set; }
    public DateTime? LastUpdateDate { get; set; }

    // Stretches the rows so the whole layout fills the screen with no scrolling. Meant for a
    // dashboard shown full-screen on a kiosk TV, where nobody is there to scroll it.
    public bool FitToScreen { get; set; }

    // Height of one grid row in pixels (the minimum when FitToScreen is on). The designer offers
    // Compact / Comfortable / Spacious, but any value in range works.
    public const int MinRowHeight = 60;
    public const int MaxRowHeight = 240;
    public const int DefaultRowHeight = 120;

    public int RowHeight { get; set; } = DefaultRowHeight;

    public virtual List<DashboardWidget> Widgets { get; set; } = [];
}

public class DashboardWidget
{
    public DashboardWidget()
    {
        Id = Guid.NewGuid();

        // A newly constructed widget is one the user just added, so it starts active. EF assigns
        // mapped properties after construction, so widgets loaded from the database keep the
        // stored value.
        IsActive = true;
    }

    public Guid Id { get; set; }

    public Guid DashboardId { get; set; }
    public Dashboard? Dashboard { get; set; }

    public required WidgetType Type { get; set; }
    public string? Title { get; set; }

    public int GridX { get; set; }
    public int GridY { get; set; }
    public int GridW { get; set; }
    public int GridH { get; set; }

    // Shows Title as a small header above the widget.
    public bool ShowTitle { get; set; }

    // On a phone the grid collapses to a single column. These let a dashboard choose what that
    // column holds: HideOnPhone leaves a widget out there, and PhoneOrder sets its place in the
    // stack (widgets without one follow, top-to-bottom then left-to-right, as on the desktop grid).
    public bool HideOnPhone { get; set; }
    public int? PhoneOrder { get; set; }

    public bool IsActive { get; set; }

    // "Show only when…": conditions (as automation rules use) that must all be true for the widget
    // to show on a live dashboard. Null means always shown. See WidgetShowConditions.
    public string? ShowConditionsJson { get; set; }
}

public class DigitalClockWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public DigitalClockWidget()
    {
        Type = WidgetType.DigitalClock;

        Is24HourFormat = false;
        ShowMilliSeconds = false;
        ShowDate = true;

        GridW = 2;
        GridH = 1;
    }

    public bool ShowMilliSeconds { get; set; }
    public bool Is24HourFormat { get; set; }
    public bool ShowDate { get; set; }
}

public class TextAreaWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public TextAreaWidget()
    {
        Type = WidgetType.TextArea;

        GridW = 2;
        GridH = 2;
    }

    public string? Content { get; set; }

    // Lets the text be changed on a kiosk screen showing it (an Edit button appears there), for
    // a notice board staff update in place. While it's on, the kiosk owns the text: the designer
    // shows it read-only, and saving the dashboard doesn't overwrite what was typed on the kiosk.
    public bool EditableOnKiosk { get; set; }

    // With kiosk editing on: true puts the editor straight on the kiosk screen and saves shortly
    // after typing stops; false shows an Edit button and saves when Save is pressed.
    public bool KioskAutoSave { get; set; }
}

public class ClientZoneLaserGameStatusWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public ClientZoneLaserGameStatusWidget()
    {
        Type = WidgetType.ClientZoneLaserGameStatus;

        GridW = 4;
        GridH = 2;

        ShowCurrentGameStatus = true;
        ShowTimeLeft = true;
    }

    public bool ShowTimeLeft { get; set; } = false;
    public bool ShowCurrentGameStatus { get; set; } = false;

    // Off by default, so status widgets that existed before game history keep looking the same.
    public bool ShowGameHistory { get; set; } = false;

    // How many finished games the history list shows, newest first.
    public int GameHistoryCount { get; set; } = DefaultGameHistoryCount;

    public Guid? ClientId { get; set; }

    public const int DefaultGameHistoryCount = 5;
    public const int MinGameHistoryCount = 1;
    public const int MaxGameHistoryCount = 20;

    public static int ClampGameHistoryCount(int count) => Math.Clamp(count, MinGameHistoryCount, MaxGameHistoryCount);
}

public class ClientZoneLaserScoreboardWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public ClientZoneLaserScoreboardWidget()
    {
        Type = WidgetType.ClientZoneLaserScoreboard;

        GridW = 4;
        GridH = 2;
    }

    public Guid? ClientId { get; set; }
}

public class ButtonWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public ButtonWidget()
    {
        Type = WidgetType.Button;

        GridW = 2;
        GridH = 1;

        Appearance = ButtonAppearance.Primary;
        Label = "Click me!";
        ActionType = ButtonActionType.TriggerProjectionProgram;
    }

    public string? Label { get; set; }
    public ButtonAppearance Appearance { get; set; } = ButtonAppearance.Primary;

    // Nullable so button rows created before this column existed read as "no action configured"
    public ButtonActionType? ActionType { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? ProjectionProgramId { get; set; }

    // Which monitor the projection opens on; null uses the client's default display.
    public int? DisplayIndex { get; set; }

    // Only meaningful for ActionType == SkipProjectionProgramStep. False (the default) skips
    // forward, matching the runner's SkipToNextStep being the more common transport action.
    public bool SkipToPreviousStep { get; set; }
}

public class MusicPlaylistSelectorWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public MusicPlaylistSelectorWidget()
    {
        Type = WidgetType.MusicPlaylistSelector;

        GridW = 3;
        GridH = 3;
    }

    public Guid? ClientId { get; set; }
}

public class MusicTimelineWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public MusicTimelineWidget()
    {
        Type = WidgetType.MusicTimeline;

        GridW = 6;
        GridH = 2;

        ShowSongTitle = true;
    }

    public Guid? ClientId { get; set; }
    public bool ShowSongTitle { get; set; } = true;

    // Off by default so dashboards saved before this option keep their look; the slider and EQ
    // button make the widget taller, which a small wall-screen tile may not have room for.
    public bool ShowVolumeAndEq { get; set; }
}

// Deliberately not scoped to a single ClientId like the other client-facing widgets: the
// point of this one is an at-a-glance roster of every kiosk, sorted offline-first.
public class KioskHealthWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public KioskHealthWidget()
    {
        Type = WidgetType.KioskHealth;

        GridW = 4;
        GridH = 3;
    }

    public bool OnlyShowOffline { get; set; }
}

// The Operations alerts list for the locations the viewer manages. Shows nothing but a sign-in
// prompt on a kiosk dashboard, which runs signed out (see OperationsAlertService).
public class OperationsAlertsWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public OperationsAlertsWidget()
    {
        Type = WidgetType.OperationsAlerts;

        GridW = 4;
        GridH = 3;
    }

    // Null shows every severity.
    public OperationsAlertSeverity? AlertMinimumSeverity { get; set; }

    public bool AlertOnlyUnacknowledged { get; set; }
}

// Today's checklists at the location the viewer is signed in to (or clocked in at), with how far
// each has got. Read-only; shows a sign-in prompt on a signed-out kiosk dashboard.
public class ChecklistsTodayWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public ChecklistsTodayWidget()
    {
        Type = WidgetType.ChecklistsToday;

        GridW = 3;
        GridH = 3;
    }

    // Leave out checklists that are already done, so the widget is a "still to do" board.
    public bool ChecklistsHideComplete { get; set; }
}

// One chosen checklist, with its tasks, for a wall screen such as the laser kiosk's dashboard.
// On a kiosk nobody is signed in: ticking only works there when ChecklistCardAllowKioskTicking is
// on, and the tick records the kiosk, not a person. Signed-in viewers see it read-only.
public class ChecklistCardWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public ChecklistCardWidget()
    {
        Type = WidgetType.Checklist;

        GridW = 4;
        GridH = 5;
    }

    public Guid? ChecklistCardChecklistId { get; set; }

    public bool ChecklistCardAllowKioskTicking { get; set; }

    // Once every task is ticked, the card's space goes empty.
    public bool ChecklistCardHideWhenDone { get; set; }
}

public class AutomationRuleStatusWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public AutomationRuleStatusWidget()
    {
        Type = WidgetType.AutomationRuleStatus;

        GridW = 3;
        GridH = 2;
    }

    public Guid? AutomationRuleId { get; set; }
}

// Leaderboard of the best results recorded by GameResultService over a rolling period. Unlike the
// live scoreboard widget next door, this reads persisted history rather than the in-memory store,
// so it survives a restart and can look further back than the current game.
public class HallOfFameWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public HallOfFameWidget()
    {
        Type = WidgetType.HallOfFame;

        GridW = 4;
        GridH = 3;

        Period = HallOfFamePeriod.Today;

        ShowTopScore = true;
        ShowBestAccuracy = true;
        ShowBestTeam = true;
    }

    public HallOfFamePeriod Period { get; set; } = HallOfFamePeriod.Today;

    public bool ShowTopScore { get; set; } = true;
    public bool ShowBestAccuracy { get; set; } = true;
    public bool ShowBestTeam { get; set; } = true;

    // Null is venue-wide, matching the other client-scoped widgets. Clients carry no LocationId,
    // so there is no per-location option here.
    public Guid? ClientId { get; set; }
}

// Per-viewer rather than per-dashboard: the current user is resolved at render time, so a single
// shared dashboard shows each staff member their own training rather than a fixed person's.
public class MyTrainingWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public MyTrainingWidget()
    {
        Type = WidgetType.MyTraining;

        GridW = 4;
        GridH = 3;

        MaxItems = 5;
    }

    public bool IncludeCompleted { get; set; }
    public int MaxItems { get; set; }
}

// Mirrors the greeting card on the standard home page - a time-of-day greeting plus the signed-in
// user's name. Both render the shared GreetingCard component, so there is nothing to configure.
public class GreetingWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public GreetingWidget()
    {
        Type = WidgetType.Greeting;

        GridW = 4;
        GridH = 1;
    }
}

// A live "now playing" view of ProjectionProgramRunnerService for one client/display, reusing the
// same progress-bar/step-label markup and runner events as the manager's Live Projection Control
// panel. ShowControls off gives a read-only view suitable for a public kiosk dashboard; on, it adds
// the same pause/skip/stop transport buttons that panel has.
public class ProjectionStatusWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public ProjectionStatusWidget()
    {
        Type = WidgetType.ProjectionStatus;

        GridW = 4;
        GridH = 2;

        ShowControls = false;
    }

    public Guid? ClientId { get; set; }

    // Which monitor to watch; null uses the client's default display, matching ButtonWidget.
    public int? DisplayIndex { get; set; }

    public bool ShowControls { get; set; }
}

// Surfaces the live Staff Announcements for the viewer's own location. Per-viewer rather than
// per-dashboard, like MyTrainingWidget: the location comes from whoever is looking, so one shared
// dashboard shows each site its own notices.
public class AnnouncementsWidget : DashboardWidget
{
    // Single source of truth for the cap. The config dialog's Max, its save-time clamp, the
    // widget's fetch size and AnnouncementService's own clamp all have to agree - when the fetch
    // size alone was lower, a widget configured above it silently rendered a truncated list.
    public const int MaxSupportedItems = 50;

    [SetsRequiredMembers]
    public AnnouncementsWidget()
    {
        Type = WidgetType.Announcements;

        GridW = 4;
        GridH = 3;

        MaxItems = 3;
    }

    public int MaxItems { get; set; }
}

// The viewer's own upcoming shifts, and optionally their time off. Per-viewer like
// MyTrainingWidget: one shared dashboard shows each staff member their own rota.
public class MyShiftsWidget : DashboardWidget
{
    public const int MaxSupportedItems = 20;
    public const int MaxDaysAhead = 42;

    [SetsRequiredMembers]
    public MyShiftsWidget()
    {
        Type = WidgetType.MyShifts;

        GridW = 4;
        GridH = 3;

        MaxItems = 5;
        DaysAhead = 14;
        ShowTimeOff = true;
    }

    public int MaxItems { get; set; }
    public int DaysAhead { get; set; }
    public bool ShowTimeOff { get; set; }
}

// Everyone on the rota at one location today, and whether they've clocked in. The location is
// the one picked in the widget settings, or else the viewer's own. With nobody signed in (a
// kiosk screen) it only shows when a location has been picked, and then by first name only,
// since those screens can face customers.
public class WhoIsOnTodayWidget : DashboardWidget
{
    [SetsRequiredMembers]
    public WhoIsOnTodayWidget()
    {
        Type = WidgetType.WhoIsOnToday;

        GridW = 4;
        GridH = 4;

        ShowClockStatus = true;
    }

    public int? LocationId { get; set; }
    public bool ShowClockStatus { get; set; }
}

// Time-off requests waiting for the viewing manager's decision at their location. Shows nothing
// useful to anyone who can't decide them.
public class PendingTimeOffWidget : DashboardWidget
{
    public const int MaxSupportedItems = 20;

    [SetsRequiredMembers]
    public PendingTimeOffWidget()
    {
        Type = WidgetType.PendingTimeOff;

        GridW = 4;
        GridH = 3;

        MaxItems = 5;
    }

    public int MaxItems { get; set; }
}

// Overdue training and expired or expiring staff documents for the viewing Admin/Manager. Shows
// nothing to anyone else, including a signed-out kiosk. LocationId only applies to an Admin
// (null = every location in their company); a Manager always sees their own location.
public class ComplianceSummaryWidget : DashboardWidget
{
    public const int MaxSupportedItems = 20;

    [SetsRequiredMembers]
    public ComplianceSummaryWidget()
    {
        Type = WidgetType.ComplianceSummary;

        GridW = 4;
        GridH = 4;

        MaxItems = 3;
        ExpiryWindowDays = ComplianceQuery.DefaultExpiryWindowDays;
    }

    public int MaxItems { get; set; }
    public int ExpiryWindowDays { get; set; }
    public int? LocationId { get; set; }
}

// Demand against staffing over the last RangeDays (ending yesterday): labour hours per game and the
// hours most out of line, for the viewing Admin/Manager only - never a signed-out kiosk. LocationId
// only applies to an Admin (null = the location they signed in to); a Manager sees their own.
public class DemandStaffingWidget : DashboardWidget
{
    public const int MinRangeDays = 14;

    [SetsRequiredMembers]
    public DemandStaffingWidget()
    {
        Type = WidgetType.DemandStaffing;

        GridW = 4;
        GridH = 4;

        RangeDays = InsightsQuery.DefaultRangeDays;
    }

    public int RangeDays { get; set; }
    public int? LocationId { get; set; }
}

// Today at one location from What's On: the day's totals and the parties still to come, for the
// viewing Admin/Manager only - never a signed-out kiosk (parties carry children's names).
// LocationId only applies to an Admin (null = the location they signed in to); a Manager sees their own.
public class WhatsOnTodayWidget : DashboardWidget
{
    public const int MaxSupportedItems = 20;

    [SetsRequiredMembers]
    public WhatsOnTodayWidget()
    {
        Type = WidgetType.WhatsOnToday;

        GridW = 4;
        GridH = 4;

        MaxItems = 6;
    }

    public int MaxItems { get; set; }
    public int? LocationId { get; set; }
}
