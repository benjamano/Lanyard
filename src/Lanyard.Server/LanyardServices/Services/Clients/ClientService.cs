using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Lanyard.Application.Services;

public class ClientService(IDbContextFactory<ApplicationDbContext> factory, 
    IHubContext<SignalRControlHub> hubContext, 
    ILogger<ClientService> logger, 
    IMemoryCache cache) : IClientService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly ILogger<ClientService> _logger = logger;
    private readonly IMemoryCache _cache = cache;

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
            _logger.LogError("Error getting client from ID: {Message}", ex.Message);
            return Result<Client?>.Fail(ex.Message);
        }
    }

    public async Task<Result<string?>> GetClientCurrentConnectionIdAsync(Guid clientId)
    {
        try
        {
            if (_cache.TryGetValue(clientId, out string? cachedConnectionId))
            {
                return Result<string?>.Ok(cachedConnectionId);
            }

            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? connectionId = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == clientId)
                .Select(x => x.MostRecentConnectionId)
                .FirstOrDefaultAsync();

            if (connectionId != null)
            {
                _cache.Set(clientId, connectionId, TimeSpan.FromMinutes(10));
            }

            return Result<string?>.Ok(connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error getting client connection ID: {Message}", ex.Message);
            return Result<string?>.Fail(ex.Message);
        }
    } 

    public async Task<Result<bool>> IsClientConnectedAsync(Guid clientId)
    {
        Result<IEnumerable<Client>> connectedClientsResult = await GetConnectedClientsAsync();

        if (connectedClientsResult.IsSuccess)
        {
            bool isConnected = connectedClientsResult.Data!.Any(x => x.Id == clientId);

            return Result<bool>.Ok(isConnected);
        }
        else
        {
            return Result<bool>.Fail(connectedClientsResult.Error!);
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
                        .Where(y => y.IsActive && x.Id == y.ClientId)
                        .Any(),
                    DmxEnabled = ctx.ClientAvailableDmxDevices
                        .AsNoTracking()
                        .Where(y => y.IsActive && x.Id == y.ClientId)
                        .Any(),
                    ZoneScoreboardVersion = ctx.ZoneScoreboardSettings
                        .AsNoTracking()
                        .Where(y => y.IsActive && x.Id == y.ClientId)
                        .Select(y => y.ZoneScoreboardVersion)
                        .FirstOrDefault()
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
                    .ThenInclude(x=> x!.ProjectionProgramSteps)
                        .ThenInclude(x=> x.ParameterValues)
                            .ThenInclude(x=> x.Parameter)
                .ToListAsync();

            return Result<IEnumerable<ClientProjectionSettings>>.Ok(projectionSettings);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<ClientProjectionSettings>>.Fail(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<ClientProjectionSettings>>> GetClientProjectionSettingsForSendingToClientAsync(Guid clientId)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<ClientProjectionSettings> projectionSettings = await ctx.ClientProjectionSettings
                .AsNoTracking()
                .Where(x => x.ClientId == clientId)
                .Where(x => x.IsActive)
                .Include(x => x.ProjectionProgram)
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
        if (_cache.TryGetValue(connectionId, out Guid cachedClientId))
        {
            return Result<Guid>.Ok(cachedClientId);
        }

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

            _cache.Set(connectionId, client.Id, TimeSpan.FromMinutes(10));

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
                return Result<bool>.Fail("Client not found for the given client ID.");
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
                .OrderBy(x=> x.Index)
                .ToListAsync();

            return Result<IEnumerable<ClientAvailableScreen>>.Ok(screens);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<ClientAvailableScreen>>.Fail(ex.Message);
        }
    }

    public async Task<Result<Guid>> AddClientProjectionAsync(ClientProjectionSettings clientProjectionSettings)
    {
        try
        {
            if (clientProjectionSettings.ProjectionProgramId == Guid.Empty)
            {
                return Result<Guid>.Fail("Projection program ID is required.");
            }

            if (clientProjectionSettings.ClientId == Guid.Empty)
            {
                return Result<Guid>.Fail("Client ID is required.");
            }

            if (clientProjectionSettings.DisplayIndex < 0)
            {
                return Result<Guid>.Fail("Display index must be zero or greater.");
            }

            if (clientProjectionSettings.Height == null || clientProjectionSettings.Height <= 0)
            {
                return Result<Guid>.Fail("Height must be greater than zero.");
            }

            if (clientProjectionSettings.Width == null || clientProjectionSettings.Width <= 0)
            {
                return Result<Guid>.Fail("Width must be greater than zero.");
            }

            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            clientProjectionSettings.Client = null;
            clientProjectionSettings.ProjectionProgram = null;

            clientProjectionSettings.IsActive = true;

            ctx.Add(clientProjectionSettings);

            await ctx.SaveChangesAsync();

            await SendUpdatedProjectionProgramInfoToClientsAsync(clientProjectionSettings.ProjectionProgramId);

            return Result<Guid>.Ok(clientProjectionSettings.Id);
        }
        catch (Exception ex)
        {
            return Result<Guid>.Fail(ex.Message);
        }
    }
    
    public async Task<Result<bool>> DeleteClientProjectionSettingsAsync(Guid clientProjectionSettingsId)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ClientProjectionSettings? projectionSettings = await ctx.ClientProjectionSettings
                .Where(x => x.Id == clientProjectionSettingsId)
                .FirstOrDefaultAsync();

            if (projectionSettings == null)
            {
                return Result<bool>.Fail("Client projection settings not found.");
            }

            projectionSettings.IsActive = false;

            await ctx.SaveChangesAsync();

            await SendUpdatedProjectionProgramInfoToClientsAsync(projectionSettings.ProjectionProgramId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public Result<IEnumerable<ClientProjectionSettingsDTO>> ConvertIntoClientProjectionSettingsDTO(IEnumerable<ClientProjectionSettings> settings)
    {
        try
        {
            IList<ClientProjectionSettingsDTO> dtoList = [];

            foreach (ClientProjectionSettings setting in settings)
            {
                if (setting.ProjectionProgram == null)
                {
                    continue;
                }

                if (setting.Width == null || setting.Height == null)
                {
                    continue;
                }

                List<ProjectionProgramStepDTO> steps = [];

                foreach (ProjectionProgramStep step in setting.ProjectionProgram.ProjectionProgramSteps.OrderBy(x => x.SortOrder))
                {
                    List<ProjectionProgramParameterValueDTO> parameterValues = [];

                    foreach (ProjectionProgramParameterValue paramValue in step.ParameterValues)
                    {
                        ProjectionProgramStepTemplateParameterDTO param = new()
                        {
                            Name = paramValue.Parameter!.Name,
                            Description = paramValue.Parameter.Description,
                            DataType = paramValue.Parameter.DataType,
                            IsRequired = paramValue.Parameter.IsRequired
                        };

                        ProjectionProgramParameterValueDTO paramDto = new()
                        {
                            Parameter = param,
                            Value = paramValue.Value
                        };

                        parameterValues.Add(paramDto);
                    }

                    ProjectionProgramStepDTO stepDto = new()
                    {
                        SortOrder = step.SortOrder,
                        ParameterValues = parameterValues
                    };

                    steps.Add(stepDto);
                }

                ProjectionProgramDTO projectionProgram = new()
                {
                    Id = setting.ProjectionProgramId,

                    Name = setting.ProjectionProgram.Name,
                    Description = setting.ProjectionProgram.Description,

                    ProjectionProgramSteps = steps
                };

                ClientProjectionSettingsDTO dto = new()
                {
                    DisplayIndex = setting.DisplayIndex,

                    Height = setting.Height ?? 0,
                    Width = setting.Width ?? 0,

                    IsBorderless = setting.IsBorderless,
                    IsFullScreen = setting.IsFullScreen,

                    ProjectionProgram = projectionProgram,
                };

                dtoList.Add(dto);
            }

            return Result<IEnumerable<ClientProjectionSettingsDTO>>.Ok(dtoList);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<ClientProjectionSettingsDTO>>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> TriggerProjectionProgramOnClientAsync(Guid clientId, Guid projectionProgramId, int? displayIndex = null)
    {
        try
        {
            Result<Client?> getResult = await GetClientFromIdAsync(clientId);

            if (!getResult.IsSuccess || getResult.Data == null)
            {
                return Result<bool>.Fail("Failed to get client.");
            }

            string? connectionId = getResult.Data.MostRecentConnectionId;

            if (string.IsNullOrEmpty(connectionId))
            {
                return Result<bool>.Fail("Client has no active connection.");
            }

            await _hubContext.Clients.Client(connectionId).SendAsync("TriggerProjectionProgram", projectionProgramId, displayIndex);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SendUpdatedProjectionProgramInfoToClientsAsync(Guid projectionProgramId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<Client> clients = await ctx.ClientProjectionSettings
                .AsNoTracking()
                .Where(x => x.ProjectionProgramId == projectionProgramId)
                .Select(x => x.Client!)
                .Distinct()
                .ToListAsync();

            foreach (Client client in clients)
            {
                Result<IEnumerable<ClientProjectionSettings>> result = await GetClientProjectionSettingsAsync(client.Id);

                if (!result.IsSuccess)
                {
                    continue;
                }

                Result<IEnumerable<ClientProjectionSettingsDTO>> resultDto = ConvertIntoClientProjectionSettingsDTO(result.Data!);

                if (!resultDto.IsSuccess || resultDto.Data == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(client.MostRecentConnectionId)) 
                {
                    continue;
                }

                await _hubContext.Clients.Client(client.MostRecentConnectionId).SendAsync("ReceiveProjectionPrograms", resultDto.Data);
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task SetClientAvailableDmxDevicesAsync(Guid clientId, IEnumerable<string> dmxDevices)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Client? client = await ctx.Clients
                .AsNoTracking()
                .Where(x => x.Id == clientId)
                .FirstOrDefaultAsync();

            if (client == null)
            { 
                return;
            }

            List<ClientAvailableDmxDevice> existingDevices = await ctx.ClientAvailableDmxDevices
                .TagWithCallSite()
                .Where(x => x.ClientId == clientId)
                .ToListAsync();

            foreach (ClientAvailableDmxDevice deviceFound in existingDevices)
            {
                if (!dmxDevices.Any(s => s.Equals(deviceFound.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    deviceFound.IsActive = false;
                }
            }

            uint deviceIndex = 0;

            foreach (string incoming in dmxDevices)
            {
                ClientAvailableDmxDevice? match = existingDevices.FirstOrDefault(x => x.Name.Equals(incoming, StringComparison.OrdinalIgnoreCase));

                bool shouldBePrimary = !existingDevices.Any(x => x.IsPrimaryDevice && x.DeviceIndex != deviceIndex) && deviceIndex == 0;

                if (match == null)
                {
                    ClientAvailableDmxDevice newDevice = new()
                    {
                        ClientId = clientId,
                        Name = incoming,
                        DeviceIndex = deviceIndex,
                        IsPrimaryDevice = shouldBePrimary,
                        IsActive = true
                    };

                    ctx.ClientAvailableDmxDevices.Add(newDevice);
                }
                else
                {
                    match.IsActive = true;
                    match.DeviceIndex = deviceIndex;
                    match.IsPrimaryDevice = shouldBePrimary;
                }

                deviceIndex++;
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Updated available DMX devices for client {ClientId}: {Devices}", clientId, string.Join(", ", dmxDevices));
        }
        catch (Exception ex)
        {
            _logger.LogError("Error setting client available DMX devices: {Message}", ex.Message);
        }
    }

    public async Task<Result<IEnumerable<ClientAvailableDmxDevice>>> GetClientAvailableDmxDevicesAsync(Guid clientId)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IEnumerable<ClientAvailableDmxDevice> devices = await ctx.ClientAvailableDmxDevices
                .AsNoTracking()
                .Where(x => x.ClientId == clientId)
                .Where(x => x.IsActive)
                .OrderBy(x => x.DeviceIndex)
                .ToListAsync();

            return Result<IEnumerable<ClientAvailableDmxDevice>>.Ok(devices);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<ClientAvailableDmxDevice>>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SetClientPrimaryDmxDeviceAsync(Guid clientId, Guid deviceId)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ClientAvailableDmxDevice> devices = await ctx.ClientAvailableDmxDevices
                .Where(x => x.ClientId == clientId)
                .ToListAsync();

            foreach (ClientAvailableDmxDevice device in devices)
            {
                device.IsPrimaryDevice = device.Id == deviceId;
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> RemoveClientPrimaryDevice(Guid clientId, Guid deviceId)
    {
        try
        {
            ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<ClientAvailableDmxDevice> devices = await ctx.ClientAvailableDmxDevices
                .Where(x => x.ClientId == clientId)
                .ToListAsync();

            ClientAvailableDmxDevice? device = devices.FirstOrDefault(x => x.Id == deviceId);

            if (device != null)
            {
                device.IsPrimaryDevice = false;
                
                await ctx.SaveChangesAsync();

                return Result<bool>.Ok(true);
            }

            return Result<bool>.Fail("Device not found.");
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<ClientDmxSettingsDTO?>> GetClientDmxSettings(Guid clientId)
    {
        try
        {
            using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            ClientDmxSettingsDTO? settings = await context.ClientAvailableDmxDevices
                .AsNoTracking()
                .TagWithCallSite()
                .Where(s => s.ClientId == clientId)
                .Where(x=> x.IsPrimaryDevice == true || context.ClientAvailableDmxDevices
                    .Any(y=> y.ClientId == clientId && y.IsPrimaryDevice == true) == false)
                .Select(s => new ClientDmxSettingsDTO
                {
                    DmxUsbDeviceIndex = s.DeviceIndex,
                    IsActive = s.IsActive
                })
                .Where(x=> x.IsActive == true)
                .FirstOrDefaultAsync();

            return Result<ClientDmxSettingsDTO?>.Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX settings for client {ClientId}", clientId);
            return Result<ClientDmxSettingsDTO?>.Fail("An error occurred while retrieving DMX settings.");
        }
    }
}
