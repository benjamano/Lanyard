using Lanyard.Shared.Enum;

namespace Lanyard.Shared.DTO;

/// <summary>
/// Sent by the server with <c>ClientHubMethods.RunSelfTest</c>. New fields must be optional:
/// a kiosk that hasn't updated yet ignores anything it doesn't know.
/// </summary>
public class KioskSelfTestRequestDTO
{
    // Echoed back, so the server can tell this run's reply from a late one.
    public Guid RunId { get; set; }

    // Turn every DMX channel up for a second so someone can see the lights respond. The kiosk
    // refuses while a scene is lighting the room.
    public bool FlashDmx { get; set; }
}

/// <summary>The kiosk's reply to <c>ClientHubMethods.RunSelfTest</c>: one entry per check it ran.</summary>
public class KioskSelfTestResultDTO
{
    public Guid RunId { get; set; }

    public string? ClientVersion { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime FinishedUtc { get; set; }

    public List<KioskSelfTestCheckDTO> Checks { get; set; } = [];
}

public class KioskSelfTestCheckDTO
{
    // One of SelfTestCheckKeys. Unknown keys from a newer kiosk are still shown.
    public string Key { get; set; } = string.Empty;

    public SelfTestOutcome Outcome { get; set; }

    // One of SelfTestCodes; the server turns it into a fix hint, so hints can change without a
    // kiosk release.
    public string? Code { get; set; }

    // What the kiosk saw, e.g. a device name or version. At most SelfTestRules.MaxDetailLength.
    public string? Detail { get; set; }
}
