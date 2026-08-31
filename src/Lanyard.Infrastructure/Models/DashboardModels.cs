using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Lanyard.Infrastructure.Enum;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.Infrastructure.Models;

public class Dashboard
{
    public Guid Id { get; set; }

    [Required]
    [StringLength(100, ErrorMessage = "The Name field can not be longer than 100 characters.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "The Description field can not be longer than 500 characters.")]
    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreateDate { get; set; }
    public DateTime? LastUpdateDate { get; set; }

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

    public bool IsActive { get; set; }
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

    public Guid? ClientId { get; set; }
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

// Surfaces the live Staff Announcements for the viewer's own location. Per-viewer rather than
// per-dashboard, like MyTrainingWidget: the location comes from whoever is looking, so one shared
// dashboard shows each site its own notices.
public class AnnouncementsWidget : DashboardWidget
{
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
