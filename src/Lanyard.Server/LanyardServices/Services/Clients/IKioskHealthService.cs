using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services.Clients;

public interface IKioskHealthService
{
    /// <summary>
    /// Stores a kiosk's heartbeat as its latest snapshot. Ok(false) means the report arrived too
    /// soon after the previous one and was ignored.
    /// </summary>
    Task<Result<bool>> RecordReportAsync(Guid clientId, KioskHealthReportDTO report);

    /// <summary>Health of every kiosk visible to the caller, keyed by client id.</summary>
    Task<Result<Dictionary<Guid, KioskHealthView>>> GetHealthAsync();
}
