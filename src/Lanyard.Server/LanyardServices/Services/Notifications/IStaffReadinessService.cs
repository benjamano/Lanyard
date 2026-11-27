using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Notifications;

// One person's set-up, as the managers' Staff Set-up list shows it.
public record StaffReadinessRow(
    string UserId,
    string Name,
    int InstalledDevices,
    int PushDevices,
    bool HasPasskey,
    DateTime? LastAppUseUtc)
{
    public bool HasApp => InstalledDevices > 0;

    public bool HasNotifications => PushDevices > 0;

    // Matches what Get Started asks of a phone: the app installed and notifications on.
    public bool IsReady => HasApp && HasNotifications;
}

// Who at a location has finished Get Started, so managers can chase anyone who hasn't.
public interface IStaffReadinessService
{
    Task<Result<List<StaffReadinessRow>>> GetAsync(LocationScope scope);
}
