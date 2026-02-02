using Lanyard.Shared.Enum;

namespace Lanyard.Infrastructure.Models;

public class Client
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public string? MostRecentConnectionId { get; set; }
    public string? MostRecentIpAddress { get; set; }

    public DateTime? LastLogin { get; set; }
    public DateTime? LastUpdateDate { get; set; }

    public DateTime CreateDate { get; set; }
}

public class ClientProjectionSettings
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public int DisplayIndex { get; set; } = 0;

    public Guid ProjectionProgramId { get; set; }
    public ProjectionProgram? ProjectionProgram { get; set; }

    //UNUSED
    public bool IsFullScreen { get; set; } = true;
    //UNUSED
    public bool IsBorderless { get; set; } = true;

    public int? Width { get; set; }
    public int? Height { get; set; }

    public bool IsActive { get; set; }
}

public class ClientAvailableScreen
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public required string Name { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    public int Index { get; set; }

    public bool IsActive { get; set; }
}