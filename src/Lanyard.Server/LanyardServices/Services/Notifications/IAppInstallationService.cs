using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Notifications;

// How a manager sees someone's reachability: "App installed on 1 device · Notifications on 2 devices".
public record DeviceReachSummary(int InstalledDevices, int PushDevices);

public interface IAppInstallationService
{
    // Called when Lanyard is opened as an installed app. DeviceId is the browser's own random id.
    Task<Result<bool>> RecordAsync(string userId, string deviceId, DeviceReport device);

    Task<Result<DeviceReachSummary>> GetSummaryAsync(string userId);

    // Installs not opened since the cutoff (uninstalled, or the phone replaced).
    Task<Result<int>> RemoveStaleAsync(DateTime cutoffUtc);
}
