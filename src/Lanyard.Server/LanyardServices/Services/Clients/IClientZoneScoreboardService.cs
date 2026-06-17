using System.Net.NetworkInformation;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.ZoneScoreboard;

namespace Lanyard.Application.Services.Clients;

public interface IClientZoneScoreboardService
{
    Task<Result<ZoneScoreboardSettings?>> GetZoneScoreboardSettingsAsync(Guid clientId);
    Task<Result<List<ClientAvailableNetworkInterface>>> GetClientAvailableNetworkInterfacesAsync(Guid clientId);
    Task<Result<bool>> UpdateClientAvailableNetworkInterfacesAsync(Guid clientId, IEnumerable<PhysicalAddress> interfaces);
    Task<Result<bool>> UpdateZoneScoreboardSettingsAsync(ZoneScoreboardSettings settings);
}