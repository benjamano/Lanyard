using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.ClientReleases;

// Platform-wide release management. A release is shared by every company, so each of these is
// PlatformAdmin only - checked here, not just on the page.
public interface IClientReleaseAdminService
{
    Task<Result<IReadOnlyList<ClientReleaseOverviewDTO>>> GetOverviewAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    // Makes a canary (prerelease) the stable release, so every Stable PC moves to it.
    Task<Result<bool>> PromoteAsync(string version, CancellationToken cancellationToken = default);

    // Stops a bad release being anyone's target. PCs on it move back to the newest release still
    // allowed for their ring the next time they check.
    Task<Result<bool>> WithdrawAsync(string version, string reason, CancellationToken cancellationToken = default);

    // Undoes a withdrawal made by mistake.
    Task<Result<bool>> RestoreAsync(string version, CancellationToken cancellationToken = default);
}
