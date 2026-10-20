using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.SignalR.Client;

namespace Lanyard.Client.SignalR;

public interface ISignalRClient
{
    Task Connect(List<Action<HubConnection>> registrations);
    Task SendLaserGameStatusAsync(LaserGameStatusDTO status);
    Task<string> IssueKioskTokenAsync();

    bool IsConnected { get; }

    /// <summary>
    /// Raised with true when the hub connects or reconnects, and false when it drops. The kiosk's
    /// offline fallback watches this to decide when the server is unreachable.
    /// </summary>
    event Action<bool>? ConnectionStateChanged;

    /// <summary>Sends a heartbeat. True when the server stored it.</summary>
    Task<bool> ReportKioskHealthAsync(KioskHealthReportDTO report);
}
