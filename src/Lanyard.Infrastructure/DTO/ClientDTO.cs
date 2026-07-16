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