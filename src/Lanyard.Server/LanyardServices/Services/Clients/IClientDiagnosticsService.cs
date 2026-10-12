using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services;

public interface IClientDiagnosticsService
{
    /// <summary>
    /// Asks a connected kiosk for its most recent log lines and keeps the reply as that
    /// client's latest log tail. <paramref name="requestedBy"/> is recorded for the audit log
    /// and shown alongside the result.
    /// </summary>
    Task<Result<ClientLogTailCapture>> RequestLogTailAsync(Guid clientId, string requestedBy);

    /// <summary>The client's latest log tail if one was fetched in the last few minutes, else null.</summary>
    Result<ClientLogTailCapture?> GetLatestLogTail(Guid clientId);
}
