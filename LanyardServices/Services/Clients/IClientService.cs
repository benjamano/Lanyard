using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
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
}
