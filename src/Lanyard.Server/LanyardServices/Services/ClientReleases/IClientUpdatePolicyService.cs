using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.Enum;

namespace Lanyard.Application.Services.ClientReleases;

public interface IClientUpdatePolicyService
{
    Task<Result<ClientUpdateTargetDTO>> GetTargetAsync(Guid clientId, CancellationToken cancellationToken = default);

    // Targets for every client the caller can see (their own company's), keyed by client ID.
    Task<Result<IReadOnlyDictionary<Guid, ClientUpdateTargetDTO>>> GetTargetsAsync(CancellationToken cancellationToken = default);

    // Same, for clients the caller has already loaded: only the release catalogue is read. Each
    // client needs Id, UpdateRing, PinnedClientVersion and ReportedClientVersion filled in.
    Task<Result<IReadOnlyDictionary<Guid, ClientUpdateTargetDTO>>> GetTargetsAsync(IEnumerable<Client> clients, CancellationToken cancellationToken = default);

    // Admin only (enforced by the page); company-scoped by the tenant filter, so a client in
    // another company reads as not found.
    Task<Result<ClientUpdateTargetDTO>> SetUpdateRingAsync(Guid clientId, ClientUpdateRing ring, string? pinnedVersion, CancellationToken cancellationToken = default);

    // What the kiosk itself asks for when it checks for updates. Never fails for "nothing to do":
    // an unknown client, or one with no target, gets an empty feed and stays on its current version.
    Task<Result<ClientUpdateFeedResponse>> GetFeedForClientAsync(Guid clientId, CancellationToken cancellationToken = default);
}
