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

    /// <summary>
    /// The client's latest log tail if one was fetched in the last few minutes, else null. Fails
    /// for a kiosk the caller can't see, so one company can't read another's capture.
    /// </summary>
    Task<Result<ClientLogTailCapture?>> GetLatestLogTailAsync(Guid clientId);

    /// <summary>
    /// Asks a connected kiosk for a screenshot of each projection window, one screen at a time, and
    /// keeps the result as that client's latest. A screen that can't be captured carries its reason
    /// instead of failing the whole request. <paramref name="requestedBy"/> is recorded for the
    /// audit log and shown alongside the result.
    /// </summary>
    Task<Result<ClientScreenshotCapture>> RequestScreenshotsAsync(Guid clientId, string requestedBy);

    /// <summary>
    /// The client's latest screenshots if taken in the last few minutes, else null. Fails for a
    /// kiosk the caller can't see.
    /// </summary>
    Task<Result<ClientScreenshotCapture?>> GetLatestScreenshotsAsync(Guid clientId);
}
