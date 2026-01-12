using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Application.Services;

public class ClientService(IDbContextFactory<ApplicationDbContext> factory) : IClientService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<Client?>> GetClientFromIdAsync(Guid clientId)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Client? client = await ctx.Clients
                .Where(x => x.Id == clientId)
                .FirstOrDefaultAsync();

            return Result<Client?>.Ok(client);

        } catch (Exception ex)
        {
            return Result<Client?>.Fail(ex.Message);
        }
    }

    public async Task<Result<Client?>> CreateClientAsync(Client newClient)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ctx.Clients.Add(newClient);

            await ctx.SaveChangesAsync();

            return Result<Client?>.Ok(newClient);
        }
        catch (Exception ex)
        {
            return Result<Client?>.Fail(ex.Message);
        }
    }

    public async Task<Result<Client?>> UpdateClientAsync(Client updatedClient)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ctx.Clients.Update(updatedClient);

            await ctx.SaveChangesAsync();

            return Result<Client?>.Ok(updatedClient);
        }
        catch (Exception ex)
        {
            return Result<Client?>.Fail(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<Client>>> GetConnectedClientsAsync()
    {
        try
        {
            List<string> ids = SignalRControlHub.ConnectedIds.ToList();

            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<Client> clients = await ctx.Clients
                .Where(x => ids.Contains(x.MostRecentConnectionId ?? ""))
                .ToListAsync();

            return Result<IEnumerable<Client>>.Ok(clients);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<Client>>.Fail(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<ClientConnectedDTO>>> GetClientsAsync()
    {
        try
        {
            List<string> ids = SignalRControlHub.ConnectedIds.ToList();

            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<ClientConnectedDTO> clients = await ctx.Clients
                .Select(x=> new ClientConnectedDTO
                {
                    Id = x.Id,
                    Name = x.Name,
                    Notes = x.Notes,
                    MostRecentIpAddress = x.MostRecentIpAddress,
                    MostRecentConnectionId = x.MostRecentConnectionId,
                    LastLogin = x.LastLogin,
                    LastUpdateDate = x.LastUpdateDate,
                    CreateDate = x.CreateDate,
                    IsCurrentlyConnected = ids.Contains(x.MostRecentConnectionId ?? "")
                })
                .ToListAsync();

            return Result<IEnumerable<ClientConnectedDTO>>.Ok(clients);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<ClientConnectedDTO>>.Fail(ex.Message);
        }
    }
}
