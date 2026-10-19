using Lanyard.Infrastructure.Enum;
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

    // Which venue the kiosk is in, so it follows that location's music schedule. Null until an
    // Admin assigns one. Only ClientService.SetClientLocationAsync writes it.
    public int? LocationId { get; set; }
    public Location? Location { get; set; }

    // Set when someone changes the music by hand while the location's schedule is on: the
    // schedule leaves this kiosk alone until then (the start of its next part of the day). Kept in
    // the database so a server restart doesn't undo a manager's choice. Only MusicScheduleEngine
    // writes it.
    public DateTime? MusicScheduleOverrideUntilUtc { get; set; }

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

    // How the kiosk authenticated on its latest hub connection. Null until it connects once after
    // per-kiosk credentials shipped. Only SignalRControlHub.OnConnectedAsync writes it.
    public ClientAuthMethod? LastAuthMethod { get; set; }

    public List<ClientCredential> Credentials { get; set; } = [];
}

/// <summary>
/// A kiosk's own secret, so the server knows which kiosk is calling rather than trusting the id it
/// claims. Only a SHA-256 hash is kept: the raw value goes to the kiosk once, when it is issued.
/// A kiosk has at most one active credential (ActivatedUtc set) plus, while a rotation is waiting
/// for the kiosk to confirm it saved the new one, one pending credential (ActivatedUtc null).
/// Never deleted: revoking sets RevokedUtc and IsActive = false, which keeps the history.
/// </summary>
public class ClientCredential
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    [MaxLength(64)]
    public string SecretHash { get; set; } = string.Empty;

    public ClientCredentialSource Source { get; set; }

    public DateTime CreatedUtc { get; set; }

    // The Admin who paired or rotated it. Null when a kiosk exchanged the legacy shared secret.
    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }

    public DateTime? ActivatedUtc { get; set; }

    // Throttled (see KioskCredentialService), so it is "roughly when", not an audit trail.
    public DateTime? LastUsedUtc { get; set; }

    public DateTime? RevokedUtc { get; set; }

    [MaxLength(450)]
    public string? RevokedByUserId { get; set; }

    [MaxLength(200)]
    public string? RevokeReason { get; set; }

    public bool IsActive { get; set; } = true;
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

    public bool? OfflineFallbackReady { get; set; }
    public int? OfflineVideosNotCached { get; set; }
    public long? OfflineCacheBytes { get; set; }
    public DateTime? OfflineCacheSyncedUtc { get; set; }
    public DateTime? LastOfflineStartedUtc { get; set; }
    public DateTime? LastOfflineEndedUtc { get; set; }

    // Alert state, written only by KioskAlertMonitorHostedService (heartbeats never touch it). Kept
    // in the database so a redeploy neither repeats an alert nor forgets to say a kiosk is back.

    /// <summary>
    /// Set once an "offline" alert has gone out: when that outage started, so "back online" can say
    /// how long it lasted. Cleared once the kiosk is back.
    /// </summary>
    public DateTime? OfflineAlertedSinceUtc { get; set; }

    /// <summary>When the kiosk was first seen degraded this time; cleared once it's healthy.</summary>
    public DateTime? DegradedSinceUtc { get; set; }

    /// <summary>The problems already alerted for, one per line, so only a new one alerts again.</summary>
    [MaxLength(500)]
    public string? AttentionAlertReasons { get; set; }
}

/// <summary>
/// One hardware self-test an Admin ran on a kiosk from the Clients page, with the checklist the
/// kiosk sent back. Kept for <c>KioskSelfTestOptions.RetentionDays</c>, then purged. Runs where the
/// kiosk never answered are kept too, because "it didn't respond" is useful history.
/// </summary>
public class ClientSelfTestRun : ICompanyOwned
{
    public Guid Id { get; set; }
    public int CompanyId { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    // Nullable so a GDPR erasure can clear it; the name is read through the link, never copied.
    public string? RequestedByUserId { get; set; }
    public UserProfile? RequestedByUser { get; set; }

    public DateTime RequestedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public ClientSelfTestStatus Status { get; set; }

    [StringLength(500)]
    public string? Error { get; set; }

    [StringLength(ClientVersionFormat.MaxLength)]
    public string? ClientVersion { get; set; }

    public bool FlashRequested { get; set; }

    public List<ClientSelfTestCheck> Checks { get; set; } = [];
}

/// <summary>One row of a self-test's checklist, as the kiosk reported it.</summary>
public class ClientSelfTestCheck
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }
    public ClientSelfTestRun? Run { get; set; }

    // The kiosk's order, which groups checks by hardware.
    public int SortOrder { get; set; }

    // A Lanyard.Shared.Kiosk.SelfTestCheckKeys value.
    [StringLength(50)]
    public string Key { get; set; } = string.Empty;

    public SelfTestOutcome Outcome { get; set; }

    // A Lanyard.Shared.Kiosk.SelfTestCodes value.
    [StringLength(50)]
    public string? Code { get; set; }

    [StringLength(300)]
    public string? Detail { get; set; }
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
