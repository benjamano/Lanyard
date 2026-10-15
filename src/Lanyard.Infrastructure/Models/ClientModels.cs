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

    // Offline fallback: when the kiosk can't reach the server for OfflineAfterSeconds, its screens
    // play their last-known program from a local cache of up to ProjectionCacheLimitMb.
    // Defaults match KioskOfflineLimits (Lanyard.Shared); the migration seeds existing rows the same.
    public bool OfflineFallbackEnabled { get; set; } = true;
    public int OfflineAfterSeconds { get; set; } = 30;
    public int ProjectionCacheLimitMb { get; set; } = 2048;

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

    // Which version this kiosk should update to - see ClientUpdateRing. Only an Admin of the
    // client's own company sets these (ClientUpdatePolicyService, through the tenant filter);
    // nothing the kiosk sends can change them.
    public ClientUpdateRing UpdateRing { get; set; } = ClientUpdateRing.Stable;

    [MaxLength(ClientVersionFormat.MaxLength)]
    public string? PinnedClientVersion { get; set; }
}

/// <summary>
/// The latest health report a kiosk sent over the hub. One row per kiosk, overwritten on every
/// heartbeat - there is deliberately no history. A kiosk with no row has never reported (it runs
/// a client too old to send heartbeats), which is shown differently from a silent one.
/// </summary>
public class ClientHealthSnapshot
{
    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public DateTime FirstHeartbeatUtc { get; set; }
    public DateTime LastHeartbeatUtc { get; set; }

    public string? ClientVersion { get; set; }

    public DateTime ProcessStartedUtc { get; set; }
    public long MachineUptimeSeconds { get; set; }

    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }

    public long? SystemDriveFreeBytes { get; set; }
    public long? SystemDriveTotalBytes { get; set; }

    public bool DmxConfigured { get; set; }
    public bool DmxOpen { get; set; }

    public bool? PreferredAudioDevicePresent { get; set; }

    public bool PacketCaptureConfigured { get; set; }
    public bool PacketCaptureRunning { get; set; }

    public int ProjectionRunningCount { get; set; }
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
