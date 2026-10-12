using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;

namespace Lanyard.Application.Services.ClientReleases;

public interface IClientReleaseCatalogService
{
    // Every known kiosk release, newest first. Refreshes from GitHub at most every few minutes;
    // if GitHub can't be reached it returns what's already stored rather than failing.
    Task<Result<IReadOnlyList<ClientRelease>>> GetReleasesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    // The Velopack feed for exactly one release: its full package only, plus where to download it.
    Task<Result<ClientUpdateFeedResponse>> GetFeedForVersionAsync(string version, CancellationToken cancellationToken = default);
}
