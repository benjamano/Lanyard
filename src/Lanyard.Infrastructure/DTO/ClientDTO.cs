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
