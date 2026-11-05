using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Contracts;

namespace Lanyard.Application.Services.Clients;

public interface IKioskClientListService
{
    /// <summary>
    /// The caller's company's kiosks, as IClientService.GetClientsWithCapabilitiesAsync returns
    /// them, shared through IKioskClientListCache. forceRefresh reads the database again, for a
    /// page showing its own user's change. Callers that show a setting they've just saved
    /// elsewhere should use the uncached method instead.
    /// </summary>
    Task<Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>>> GetClientsAsync(bool forceRefresh = false);
}

public class KioskClientListService(
    IClientService clientService,
    IKioskClientListCache cache,
    ITenantProvider tenant) : IKioskClientListService
{
    private readonly IClientService _clientService = clientService;
    private readonly IKioskClientListCache _cache = cache;
    private readonly ITenantProvider _tenant = tenant;

    public async Task<Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>>> GetClientsAsync(bool forceRefresh = false)
    {
        try
        {
            // A system caller sees every company and an unresolved user sees none, so neither has a
            // company's entry to share. They read straight through.
            if (_tenant.IsSystem || _tenant.CompanyId is not int companyId)
            {
                return await LoadAsync();
            }

            if (!forceRefresh && _cache.TryGet(companyId, out IReadOnlyList<ClientConnectedWithCapabilitiesDTO> cached))
            {
                HashSet<string> connectedIds = GetConnectedConnectionIds().ToHashSet();

                return Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>>.Ok(cached.Select(x => Copy(x, connectedIds)).ToList());
            }

            long version = _cache.GetVersion(companyId);

            Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>> loaded = await LoadAsync();

            if (loaded.IsSuccess)
            {
                // The cache keeps its own copies: callers are free to change what they're given.
                _cache.TrySet(companyId, version, loaded.Data!.Select(x => Copy(x, null)).ToList());
            }

            return loaded;
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>>.Fail($"Failed to load kiosks: {ex.Message}");
        }
    }

    // Same seam as ClientService.GetConnectedConnectionIds.
    protected internal virtual IReadOnlyCollection<string> GetConnectedConnectionIds()
    {
        return SignalRControlHub.ConnectedIds;
    }

    private async Task<Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>>> LoadAsync()
    {
        Result<IEnumerable<ClientConnectedWithCapabilitiesDTO>> result = await _clientService.GetClientsWithCapabilitiesAsync();

        return result.IsSuccess
            ? Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>>.Ok(result.Data!.ToList())
            : Result<IReadOnlyList<ClientConnectedWithCapabilitiesDTO>>.Fail(result.Error ?? "Failed to load kiosks.");
    }

    // Every field GetClientsWithCapabilitiesAsync fills in. connectedIds null keeps the flag as loaded.
    private static ClientConnectedWithCapabilitiesDTO Copy(ClientConnectedWithCapabilitiesDTO x, HashSet<string>? connectedIds)
    {
        return new ClientConnectedWithCapabilitiesDTO
        {
            Id = x.Id,
            Name = x.Name,
            Notes = x.Notes,
            MostRecentIpAddress = x.MostRecentIpAddress,
            MostRecentConnectionId = x.MostRecentConnectionId,
            LastLogin = x.LastLogin,
            LastUpdateDate = x.LastUpdateDate,
            LastDisconnectDate = x.LastDisconnectDate,
            CreateDate = x.CreateDate,
            ReportedClientVersion = x.ReportedClientVersion,
            ClientVersionReportedAt = x.ClientVersionReportedAt,
            LastAuthMethod = x.LastAuthMethod,
            CompanyId = x.CompanyId,
            UpdateRing = x.UpdateRing,
            PinnedClientVersion = x.PinnedClientVersion,
            IsCurrentlyConnected = connectedIds is null
                ? x.IsCurrentlyConnected
                : x.MostRecentConnectionId != null && connectedIds.Contains(x.MostRecentConnectionId),
            ProjectionEnabled = x.ProjectionEnabled,
            DmxEnabled = x.DmxEnabled,
            ZoneScoreboardVersion = x.ZoneScoreboardVersion
        };
    }
}
