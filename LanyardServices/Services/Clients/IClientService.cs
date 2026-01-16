using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Application.Services;

public interface IClientService
{
    Task<Result<Client?>> GetClientFromIdAsync(Guid clientId);
    Task<Result<Client?>> CreateClientAsync(Client newClient);
    Task<Result<Client?>> UpdateClientAsync(Client updatedClient);
    Task<Result<IEnumerable<Client>>> GetConnectedClientsAsync();
    Task<Result<IEnumerable<ClientConnectedDTO>>> GetClientsAsync();
    Task<Result<IEnumerable<ClientConnectedWithCapabilitiesDTO>>> GetClientsWithCapabilitiesAsync();
    Task<Result<IEnumerable<ClientProjectionSettings>>> GetClientProjectionSettingsAsync(Guid clientId);
    Task<Result<Guid>> GetClientIdFromConnectionIdAsync(string connectionId);
    Task<Result<bool>> SetClientAvailableScreensAsync(Guid ClientId, IEnumerable<ClientAvailableScreenDTO> screens);
    Task<Result<IEnumerable<ClientAvailableScreen>>> GetClientAvailableScreensAsync(Guid clientId);
    Task<Result<IEnumerable<ProjectionProgram>>> GetProjectionProgramsAsync();
}
