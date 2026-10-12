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
}
