using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Numerics;
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

    public async Task<Result<IEnumerable<ClientConnectedWithCapabilitiesDTO>>> GetClientsWithCapabilitiesAsync()
    {
        try
        {
            List<string> ids = SignalRControlHub.ConnectedIds.ToList();

            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<ClientConnectedWithCapabilitiesDTO> clients = await ctx.Clients
                .Select(x => new ClientConnectedWithCapabilitiesDTO
                {
                    Id = x.Id,
                    Name = x.Name,
                    Notes = x.Notes,
                    MostRecentIpAddress = x.MostRecentIpAddress,
                    MostRecentConnectionId = x.MostRecentConnectionId,
                    LastLogin = x.LastLogin,
                    LastUpdateDate = x.LastUpdateDate,
                    CreateDate = x.CreateDate,
                    IsCurrentlyConnected = ids.Contains(x.MostRecentConnectionId ?? ""),
                    ProjectionEnabled = ctx.ClientProjectionSettings
                        .AsNoTracking()
                        .Where(x => x.IsActive && x.ClientId == x.ClientId)
                        .Any()
                })
                .ToListAsync();

            return Result<IEnumerable<ClientConnectedWithCapabilitiesDTO>>.Ok(clients);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<ClientConnectedWithCapabilitiesDTO>>.Fail(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<ClientProjectionSettings>>> GetClientProjectionSettingsAsync(Guid clientId)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<ClientProjectionSettings> projectionSettings = await ctx.ClientProjectionSettings
                .AsNoTracking()
                .Where(x=> x.ClientId == clientId)
                .Where(x => x.IsActive)
                .Include(x=> x.ProjectionProgram)
                .ToListAsync();

            return Result<IEnumerable<ClientProjectionSettings>>.Ok(projectionSettings);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<ClientProjectionSettings>>.Fail(ex.Message);
        }
    }

    public async Task<Result<Guid>> GetClientIdFromConnectionIdAsync(string connectionId)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Client? client = await ctx.Clients
                .AsNoTracking()
                .Where(x => x.MostRecentConnectionId == connectionId)
                .FirstOrDefaultAsync();

            if (client == null)
            {
                return Result<Guid>.Fail("Client not found for the given connection ID.");
            }

            return Result<Guid>.Ok(client.Id);
        }
        catch (Exception ex)
        {
            return Result<Guid>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SetClientAvailableScreensAsync(Guid ClientId, IEnumerable<ClientAvailableScreenDTO> screens)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Client? client = await ctx.Clients
                .AsNoTracking()
                .Where(x => x.Id == ClientId)
                .FirstOrDefaultAsync();

            if (client == null)
            {
                return Result<bool>.Fail("Client not found for the given connection ID.");
            }

            IEnumerable<ClientAvailableScreen> existingScreens = screens
                .Select(x => new ClientAvailableScreen
                {
                    ClientId = ClientId,
                    Name = x.Name,
                    Index = x.Index,
                    Width = x.Width,
                    Height = x.Height,
                    IsActive = true
                });

            IEnumerable<ClientAvailableScreen> screensFound = await ctx.ClientAvailableScreens
                .Where(x => x.ClientId == ClientId)
                .ToListAsync();

            foreach (ClientAvailableScreen screenFound in screensFound)
            {
                if (!existingScreens.Any(s => s.Name.Equals(screenFound.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    screenFound.IsActive = false;
                }
            }

            foreach (ClientAvailableScreen incoming in existingScreens)
            {
                ClientAvailableScreen? match = screensFound.Where(x=> x.Name.Equals(incoming.Name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

                if (match == null)
                {
                    incoming.ClientId = ClientId;
                    incoming.IsActive = true;
                    ctx.ClientAvailableScreens.Add(incoming);
                }
                else
                {
                    match.IsActive = true;
                    match.Width = incoming.Width;
                    match.Height = incoming.Height;
                    match.Index = incoming.Index;
                }
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<ClientAvailableScreen>>> GetClientAvailableScreensAsync(Guid clientId)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<ClientAvailableScreen> screens = await ctx.ClientAvailableScreens
                .AsNoTracking()
                .Where(x => x.ClientId == clientId)
                .Where(x => x.IsActive)
                .ToListAsync();

            return Result<IEnumerable<ClientAvailableScreen>>.Ok(screens);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<ClientAvailableScreen>>.Fail(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<ProjectionProgram>>> GetProjectionProgramsAsync()
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<ProjectionProgram> programs = await ctx.ProjectionPrograms
                .AsNoTracking()
                .Where(x => x.IsActive)
                .ToListAsync();

            return Result<IEnumerable<ProjectionProgram>>.Ok(programs);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<ProjectionProgram>>.Fail(ex.Message);
        }
    }
}
