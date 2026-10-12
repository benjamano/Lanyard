using Lanyard.Shared.Contracts;
using Lanyard.Shared.Enum;
using System.ComponentModel.DataAnnotations;

namespace Lanyard.Infrastructure.Models;

public class Client : ICompanyOwned
{
    public Guid Id { get; set; }
    public int CompanyId { get; set; }
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

    public int MusicCacheLimitMb { get; set; } = 500;

    public bool AutoRestartEnabled { get; set; } = false;
    public RestartIntervalUnit AutoRestartIntervalUnit { get; set; } = RestartIntervalUnit.Day;
    public int AutoRestartIntervalCount { get; set; } = 1;
    public TimeOnly AutoRestartTimeOfDay { get; set; } = new TimeOnly(4, 0);

    // Windows endpoint ID of the output device the client keeps as its default. The name is
    // kept alongside it so the setting still reads sensibly while the device is unplugged, and
    // so the client can fall back to a name match if the endpoint comes back under a new ID.
    public string? PreferredAudioDeviceId { get; set; }
    public string? PreferredAudioDeviceName { get; set; }

    // The Velopack version the kiosk said it was running when it last connected. Null means it
    // has never reported one - every client older than 1.0.41, which doesn't send it. Kept when
    // a connection arrives without a version, so an old build reconnecting can't blank it.
    [MaxLength(ClientVersionFormat.MaxLength)]
    public string? ReportedClientVersion { get; set; }
    public DateTime? ClientVersionReportedAt { get; set; }
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

public class ClientAvailableAudioDevice
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;

    public bool IsActive { get; set; }
}
