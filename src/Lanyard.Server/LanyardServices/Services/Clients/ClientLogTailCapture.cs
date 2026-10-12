using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services;

/// <summary>
/// A log tail as the server holds it: what the kiosk sent (redacted again on arrival), plus who
/// asked for it and when it arrived. Kept in memory only, see <see cref="IClientDiagnosticsStore"/>.
/// </summary>
public class ClientLogTailCapture
{
    public Guid ClientId { get; init; }

    public required ClientLogTailDTO LogTail { get; init; }

    public required string RequestedBy { get; init; }

    public DateTime ReceivedAtUtc { get; init; }

    public DateTime ExpiresAtUtc { get; init; }
}
