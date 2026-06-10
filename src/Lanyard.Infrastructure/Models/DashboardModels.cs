using System.ComponentModel.DataAnnotations;

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

    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && Name.Length <= 100 && (Description == null || Description.Length <= 500);
}

public class DashboardWidget
{
    public Guid Id { get; set; }

    public Guid DashboardId { get; set; }
    public Dashboard? Dashboard { get; set; }

    public required string Type { get; set; }
    public string? Title { get; set; }

    public int GridX { get; set; }
    public int GridY { get; set; }
    public int GridW { get; set; }
    public int GridH { get; set; }

    public int SortOrder { get; set; }

    public string ConfigJson { get; set; } = "{}";

    public bool IsActive { get; set; }
}
