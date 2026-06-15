using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Lanyard.Infrastructure.Enum;

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