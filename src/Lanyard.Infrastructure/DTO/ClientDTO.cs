using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Infrastructure.DTO;

public class ClientConnectedDTO : Client
{
    public bool IsCurrentlyConnected { get; set; }
}

public class ClientConnectedWithCapabilitiesDTO : Client
{
    public bool IsCurrentlyConnected { get; set; }

    public bool ProjectionEnabled { get; set; }
    public bool DmxEnabled { get; set; }
    public ZoneScoreboardVersion? ZoneScoreboardVersion { get; set; }
}

public class ClientMusicSettingsDTO
{
    public int CacheLimitMb { get; set; }
}

public class ClientRestartScheduleDTO
{
    public bool Enabled { get; set; }
    public RestartIntervalUnit IntervalUnit { get; set; }
    public int IntervalCount { get; set; }
    public TimeOnly TimeOfDay { get; set; }
}

public class ClientAudioSettingsDTO
{
    public string? PreferredDeviceId { get; set; }
    public string? PreferredDeviceName { get; set; }
}

public enum EqualiserApplyStatus
{
    // Saved and sent to the kiosk, which is playing with it now.
    Applied,
    // Saved; the kiosk is offline and gets it when it reconnects.
    SavedKioskOffline,
    // Saved, but the kiosk's version predates the equaliser, so it won't change until it updates.
    SavedKioskOutdated
}

// A kiosk's stored equaliser, plus whether a change would reach it right now.
public class ClientEqualiserStateDTO
{
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public Lanyard.Shared.Audio.MusicEqualiserSettings Settings { get; set; } = Lanyard.Shared.Audio.MusicEqualiserSettings.Flat;
    public bool IsConnected { get; set; }
    public bool IsSupported { get; set; }
    public string? ReportedVersion { get; set; }

    public EqualiserApplyStatus Status =>
        !IsConnected ? EqualiserApplyStatus.SavedKioskOffline
        : !IsSupported ? EqualiserApplyStatus.SavedKioskOutdated
        : EqualiserApplyStatus.Applied;
}

// What a kiosk is set to run and what that resolves to right now.
public class ClientUpdateTargetDTO
{
    public Guid ClientId { get; set; }
    public ClientUpdateRing Ring { get; set; }
    public string? PinnedVersion { get; set; }
    public string? ReportedVersion { get; set; }

    // Null when there's nothing to move to - the kiosk then stays on whatever it runs.
    public string? TargetVersion { get; set; }

    // Why there is no target, in words an admin can act on (e.g. the pinned release was withdrawn).
    public string? Problem { get; set; }

    // True when the kiosk has reported a version and it isn't the one it should be on.
    public bool IsOffTarget => TargetVersion is not null && ReportedVersion is not null && ReportedVersion != TargetVersion;
}

// One kiosk release as the Kiosk Releases page shows it.
public class ClientReleaseOverviewDTO
{
    public required ClientRelease Release { get; set; }

    public bool IsStable { get; set; }

    // How many PCs, across every company, last reported running this version.
    public int ReportedPcCount { get; set; }
}
