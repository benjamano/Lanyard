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

    // Only written on a clean SignalR disconnect - a hard server kill never runs
    // OnDisconnectedAsync - so consumers fall back to LastLogin rather than reading
    // null as "has never been offline".
    public DateTime? LastDisconnectDate { get; set; }

    public DateTime CreateDate { get; set; }

    /// <summary>
    /// The venue this kiosk physically sits in, when it has been said.
    ///
    /// Kiosks were global until receipt printing arrived, which was fine while everything they
    /// did was driven by an admin. It is not fine for a printer: without this there is nothing to
    /// compare a venue against, so a manager at one company could point their venue at another
    /// company's kiosk and print their tickets - table, dishes, allergens and the customer's own
    /// note - on someone else's paper. Nullable because an unassigned kiosk is a normal state;
    /// it simply cannot be chosen as anyone's printer.
    /// </summary>
    public int? LocationId { get; set; }
    public Location? Location { get; set; }

    public int MusicCacheLimitMb { get; set; } = 500;

    public bool AutoRestartEnabled { get; set; } = false;
    public RestartIntervalUnit AutoRestartIntervalUnit { get; set; } = RestartIntervalUnit.Day;
    public int AutoRestartIntervalCount { get; set; } = 1;
    public TimeOnly AutoRestartTimeOfDay { get; set; } = new TimeOnly(4, 0);
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

    public bool IsDarkTheme { get; set; }
    public bool ShowDebugMode { get; set; }

    public int RepeatNumberOfTimes { get; set; } = 0;
    public bool RepeatInfinitely { get; set; } = true;

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

public class ClientAvailableVideoDevice
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;

    public bool IsActive { get; set; }
}