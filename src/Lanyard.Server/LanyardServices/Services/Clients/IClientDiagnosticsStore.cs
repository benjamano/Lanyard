using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services;

public interface IClientDiagnosticsStore
{
    /// <summary>
    /// Marks a log tail as running for the client. Fails with a reason when one is already in
    /// flight or the last one started within the cooldown, so impatient clicks can't pile up.
    /// </summary>
    bool TryBeginLogTail(Guid clientId, out string? reason);

    void EndLogTail(Guid clientId);

    /// <summary>Keeps the capture as the client's latest, replacing any earlier one.</summary>
    ClientLogTailCapture SaveLogTail(Guid clientId, ClientLogTailDTO logTail, string requestedBy);

    /// <summary>The client's latest log tail, or null once it has expired.</summary>
    ClientLogTailCapture? GetLatestLogTail(Guid clientId);

    /// <summary>The same guard as <see cref="TryBeginLogTail"/>, kept separately for screenshots.</summary>
    bool TryBeginScreenshots(Guid clientId, out string? reason);

    void EndScreenshots(Guid clientId);

    /// <summary>Keeps the screenshots as the client's latest, replacing any earlier ones.</summary>
    ClientScreenshotCapture SaveScreenshots(Guid clientId, IReadOnlyList<ClientScreenSnapshot> screens, string? clientVersion, string requestedBy);

    /// <summary>The client's latest screenshots, or null once they have expired.</summary>
    ClientScreenshotCapture? GetLatestScreenshots(Guid clientId);
}
