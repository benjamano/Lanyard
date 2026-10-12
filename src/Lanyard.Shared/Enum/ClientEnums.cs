using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Shared.Enum;

public enum ClientGroup
{
    Music = 1,
    PacketSniffer = 2,
    Projector = 3
}

public enum ProjectionType
{
    Webpage = 1,
    CaptureSource = 2,
    StaticText = 3,
    VideoFile = 4,
    ImageFile = 5
}

public enum RestartIntervalUnit
{
    Day = 1,
    Week = 2,
    Month = 3
}

public enum ClientRestartType
{
    Application = 1,
    Computer = 2
}

// Which kiosk software version a client is told to run. Stored on Client; the server resolves it
// to one concrete version on every update check (see ClientTargetVersionResolver).
public enum ClientUpdateRing
{
    // The newest release an admin has promoted to everyone (or that was published as a normal release).
    Stable = 0,
    // The newest release of any kind, so this PC tries new releases before everyone else.
    Canary = 1,
    // Exactly PinnedClientVersion, until an admin changes it. Can be older than the current version.
    Pinned = 2
}
