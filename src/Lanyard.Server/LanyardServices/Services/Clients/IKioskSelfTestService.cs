using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Enum;

namespace Lanyard.Application.Services;

public interface IKioskSelfTestService
{
    /// <summary>
    /// Asks a connected kiosk to test its hardware and stores what it reports. Succeeds with the
    /// stored run whenever the request reached the kiosk, including when the kiosk then timed out
    /// or was too old (the run's Status and Error say so), and fails when it never could: the
    /// caller isn't an Admin of the kiosk's company, the kiosk is offline, or a test just ran.
    /// </summary>
    Task<Result<KioskSelfTestRunView>> RunAsync(Guid clientId, bool flashDmx);

    /// <summary>The kiosk's most recent runs, newest first. Empty for a kiosk the caller can't see.</summary>
    Task<Result<List<KioskSelfTestRunView>>> GetHistoryAsync(Guid clientId, int take = 10);
}

public class KioskSelfTestRunView
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Who ran it, or null once their account has been erased.</summary>
    public string? RequestedByName { get; set; }

    public ClientSelfTestStatus Status { get; set; }
    public string? Error { get; set; }
    public string? ClientVersion { get; set; }
    public bool FlashRequested { get; set; }

    public List<KioskSelfTestGroupView> Groups { get; set; } = [];

    public int Count(SelfTestOutcome outcome) => Groups.Sum(g => g.Checks.Count(c => c.Outcome == outcome));
}

public class KioskSelfTestGroupView
{
    public string Group { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<KioskSelfTestCheckView> Checks { get; set; } = [];
}

public class KioskSelfTestCheckView
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public SelfTestOutcome Outcome { get; set; }
    public string? Code { get; set; }
    public string? Detail { get; set; }

    /// <summary>What to do about it; null when it passed or needs nothing doing.</summary>
    public string? Hint { get; set; }
}
