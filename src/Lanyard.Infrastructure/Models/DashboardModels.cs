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