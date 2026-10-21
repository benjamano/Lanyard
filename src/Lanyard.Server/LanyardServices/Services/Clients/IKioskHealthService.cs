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

    /// <summary>
    /// Health of the given kiosks, for a caller that has already loaded them: only the snapshots
    /// are read, and each kiosk's connected flag is taken as given.
    /// </summary>
    Task<Result<Dictionary<Guid, KioskHealthView>>> GetHealthAsync(IReadOnlyCollection<KioskHealthClient> clients);

    /// <summary>Health of one kiosk, or Ok(null) if it isn't visible to the caller.</summary>
    Task<Result<KioskHealthView?>> GetClientHealthAsync(Guid clientId);
}

/// <summary>What GetHealthAsync needs to know about a kiosk the caller has already loaded.</summary>
public sealed record KioskHealthClient(Guid Id, bool IsConnected, DateTime? LastDisconnectDate);
