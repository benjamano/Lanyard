using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.ClientReleases;

public sealed class ClientUpdatePolicyService(
    IDbContextFactory<ApplicationDbContext> factory,
    IClientReleaseCatalogService catalog,
    ILogger<ClientUpdatePolicyService> logger) : IClientUpdatePolicyService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IClientReleaseCatalogService _catalog = catalog;
    private readonly ILogger<ClientUpdatePolicyService> _logger = logger;

    public async Task<Result<ClientUpdateTargetDTO>> GetTargetAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            Client? client = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == clientId, cancellationToken);

            if (client is null)
            {
                return Result<ClientUpdateTargetDTO>.Fail("Client not found.");
            }

            Result<IReadOnlyList<ClientRelease>> releases = await _catalog.GetReleasesAsync(cancellationToken: cancellationToken);

            if (!releases.IsSuccess)
            {
                return Result<ClientUpdateTargetDTO>.Fail(releases.Error ?? "Failed to load kiosk releases.");
            }

            return Result<ClientUpdateTargetDTO>.Ok(ToTarget(client, releases.Data!));
        }
        catch (Exception ex)
        {
            return Result<ClientUpdateTargetDTO>.Fail($"Failed to work out the client's target version: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyDictionary<Guid, ClientUpdateTargetDTO>>> GetTargetsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            List<Client> clients = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .ToListAsync(cancellationToken);

            Result<IReadOnlyList<ClientRelease>> releases = await _catalog.GetReleasesAsync(cancellationToken: cancellationToken);

            if (!releases.IsSuccess)
            {
                return Result<IReadOnlyDictionary<Guid, ClientUpdateTargetDTO>>.Fail(releases.Error ?? "Failed to load kiosk releases.");
            }

            Dictionary<Guid, ClientUpdateTargetDTO> targets = clients.ToDictionary(x => x.Id, x => ToTarget(x, releases.Data!));

            return Result<IReadOnlyDictionary<Guid, ClientUpdateTargetDTO>>.Ok(targets);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyDictionary<Guid, ClientUpdateTargetDTO>>.Fail($"Failed to work out target versions: {ex.Message}");
        }
    }

    public async Task<Result<ClientUpdateTargetDTO>> SetUpdateRingAsync(Guid clientId, ClientUpdateRing ring, string? pinnedVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Enum.IsDefined(ring))
            {
                return Result<ClientUpdateTargetDTO>.Fail("Unknown update setting.");
            }

            Result<IReadOnlyList<ClientRelease>> releases = await _catalog.GetReleasesAsync(cancellationToken: cancellationToken);

            if (!releases.IsSuccess)
            {
                return Result<ClientUpdateTargetDTO>.Fail(releases.Error ?? "Failed to load kiosk releases.");
            }

            string? pin = null;

            if (ring == ClientUpdateRing.Pinned)
            {
                ClientRelease? pinnedRelease = releases.Data!.FirstOrDefault(x => x.Version == pinnedVersion);

                if (pinnedRelease is null)
                {
                    return Result<ClientUpdateTargetDTO>.Fail("Choose a version to pin this PC to.");
                }

                if (pinnedRelease.WithdrawnAt != null)
                {
                    return Result<ClientUpdateTargetDTO>.Fail($"v{pinnedRelease.Version} has been withdrawn and can't be pinned.");
                }

                pin = pinnedRelease.Version;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            // Tenant-filtered: another company's client simply isn't found.
            Client? client = await ctx.Clients
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == clientId, cancellationToken);

            if (client is null)
            {
                return Result<ClientUpdateTargetDTO>.Fail("Client not found.");
            }

            client.UpdateRing = ring;
            client.PinnedClientVersion = pin;
            client.LastUpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Client {ClientId} update setting changed to {UpdateRing} (pinned version {PinnedVersion})", clientId, ring, pin);

            return Result<ClientUpdateTargetDTO>.Ok(ToTarget(client, releases.Data!));
        }
        catch (Exception ex)
        {
            return Result<ClientUpdateTargetDTO>.Fail($"Failed to save the update setting: {ex.Message}");
        }
    }

    public async Task<Result<ClientUpdateFeedResponse>> GetFeedForClientAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        Result<ClientUpdateTargetDTO> target = await GetTargetAsync(clientId, cancellationToken);

        if (!target.IsSuccess)
        {
            // A kiosk the server doesn't know yet registers itself when its hub connection opens;
            // until then it has no setting, so it stays where it is.
            _logger.LogInformation("No update target for client {ClientId}: {Error}", clientId, target.Error);
            return Result<ClientUpdateFeedResponse>.Ok(new ClientUpdateFeedResponse());
        }

        if (target.Data!.TargetVersion is not string targetVersion)
        {
            _logger.LogInformation("Client {ClientId} has no version to move to: {Problem}", clientId, target.Data.Problem);
            return Result<ClientUpdateFeedResponse>.Ok(new ClientUpdateFeedResponse());
        }

        Result<ClientUpdateFeedResponse> feed = await _catalog.GetFeedForVersionAsync(targetVersion, cancellationToken);

        if (feed.IsSuccess)
        {
            _logger.LogInformation("Client {ClientId} ({UpdateRing}) checked for updates; target is {TargetVersion}", clientId, target.Data.Ring, targetVersion);
        }

        return feed;
    }

    private static ClientUpdateTargetDTO ToTarget(Client client, IReadOnlyList<ClientRelease> releases)
    {
        ClientTargetResolution resolution = ClientTargetVersionResolver.Resolve(client.UpdateRing, client.PinnedClientVersion, releases);

        return new ClientUpdateTargetDTO
        {
            ClientId = client.Id,
            Ring = client.UpdateRing,
            PinnedVersion = client.PinnedClientVersion,
            ReportedVersion = client.ReportedClientVersion,
            TargetVersion = resolution.TargetVersion,
            Problem = resolution.Problem
        };
    }
}
