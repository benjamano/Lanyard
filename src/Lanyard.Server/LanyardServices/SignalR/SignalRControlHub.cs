using Lanyard.Application.Services;
using Lanyard.Application.Services.Clients;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.DTO.VideoDevices;
using Lanyard.Infrastructure.DTO.ZoneScoreboard;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;

namespace Lanyard.Application.SignalR;

public class SignalRControlHub(
    ILogger<SignalRControlHub> logger,
    MusicPlayerService playerService,
    IClientService clientService,
    ILaserGameStatusStore laserGameStatusStore,
    SignalRProjectionControlHubEvents hubEvents,
    AutomationEngineService automationEngineService,
    IDmxClientService dmxClientService,
    IClientZoneScoreboardService clientZoneScoreboardService) : Hub, ISignalRProjectionControlHub
{
    private readonly ILogger<SignalRControlHub> _logger = logger;

    private readonly MusicPlayerService _playerService = playerService;
    private readonly IClientService _clientService = clientService;
    private readonly SignalRProjectionControlHubEvents _hubEvents = hubEvents;
    private readonly ILaserGameStatusStore _laserGameStatusStore = laserGameStatusStore;
    private readonly AutomationEngineService _automationEngineService = automationEngineService;
    private readonly IDmxClientService _dmxClientService = dmxClientService;
    private readonly IClientZoneScoreboardService _clientZoneScoreboardService = clientZoneScoreboardService;

    private static readonly ConcurrentDictionary<string, bool> _connections = new();

    public static IReadOnlyCollection<string> ConnectedIds => (IReadOnlyCollection<string>)_connections.Keys;

    public override async Task OnConnectedAsync()
    {
        HttpContext? httpContext = Context.GetHttpContext();

        if (string.IsNullOrEmpty(httpContext?.Request.Query["clientId"].ToString()) == true)
        {
            _logger.LogWarning("Client connected without an ID, disconnecting");

            Context.Abort();
            return;
        }

        Guid clientId = Guid.Parse(httpContext?.Request.Query["clientId"].ToString()!);
        string clientIp = httpContext?.Connection.RemoteIpAddress?.ToString() ?? "Unknown";

        Result<Client?> result = await _clientService.GetClientFromIdAsync(clientId);

        Client client = new();

        if (!result.IsSuccess || result.Data == null)
        {
            _logger.LogWarning("Client connected with an unknown ID {ClientId} ({IpAddress}), creating a new client entry", clientId, clientIp);

            Client newClient = new()
            {
                Id = clientId,
                Name = "New Client",
                Notes = "",
                MostRecentConnectionId = Context.ConnectionId,
                MostRecentIpAddress = clientIp,
                CreateDate = DateTime.UtcNow,
                LastLogin = DateTime.UtcNow
            };

            Result<Client?> createResult = await _clientService.CreateClientAsync(newClient);

            if (!createResult.IsSuccess || createResult.Data == null)
            {
                _logger.LogError("Failed to create new client entry for ID {ClientId}: {Error}", clientId, createResult.Error);

                Context.Abort();
                return;
            }

            client = createResult.Data!;
        }
        else
        {
            client = result.Data!;

            client.LastUpdateDate = DateTime.UtcNow;
            client.MostRecentConnectionId = Context.ConnectionId;
            client.MostRecentIpAddress = clientIp;
            client.LastLogin = DateTime.UtcNow;

            Result<Client?> updateResult = await _clientService.UpdateClientAsync(client);

            if (!updateResult.IsSuccess)
            {
                _logger.LogError("Failed to update client entry for ID {ClientId}: {Error}", clientId, updateResult.Error);

                Context.Abort();
                return;
            }

            await SendProjectionProgramInfoToClientAsync(client.Id);
            await SendMusicSettingsToClientAsync(client);
            await SendDmxSettingsToClientAsync(client);
            await SendZoneScoreboardSettingsToClientAsync(client);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ClientGroup.Music.ToString());

        _logger.LogInformation("Client {ClientName} ({ConnectionId}) connected and added to Music group", client.Name, Context.ConnectionId);

        _connections.TryAdd(Context.ConnectionId, true);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ClientGroup.Music.ToString());

        _logger.LogInformation("Client {ConnectionId} disconnected from Music group", Context.ConnectionId);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (getClientResult.IsSuccess)
        {
            _laserGameStatusStore.RemoveStatus(getClientResult.Data);
        }

        _connections.TryRemove(Context.ConnectionId, out _);

        await base.OnDisconnectedAsync(exception);
    }

    public async Task PlaybackStateChanged(PlaybackState state)
    {
        _logger.LogInformation("Client {ConnectionId} reported playback state: {State}", Context.ConnectionId, state);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        _playerService.UpdatePlaybackState(getClientResult.Data, state);
    }

    public async Task CurrentPlayingSongChanged(Guid song)
    {
        _logger.LogInformation("Client {ConnectionId} reported new song: {Song}", Context.ConnectionId, song);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        _playerService.UpdateCurrentSong(getClientResult.Data, song);
    }

    public async Task UpdateAvailableScreens(IEnumerable<ClientAvailableScreenDTO> screens)
    {
        _logger.LogInformation("Client {ConnectionId} reported available screens: {Screens}", Context.ConnectionId, screens.Select(x => x.Name));

        Result<Guid> getResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getResult.IsSuccess)
        {
            _logger.LogError("Failed to get client ID from connection ID {ConnectionId}: {Error}", Context.ConnectionId, getResult.Error);
            return;
        }

        Guid clientId = getResult.Data!;

        await _clientService.SetClientAvailableScreensAsync(clientId, screens);
    }

    public async Task UpdateLaserGameStatus(LaserGameStatusDTO status)
    {
        Result<Guid> getResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID for laser status update from {ConnectionId}: {Error}", Context.ConnectionId, getResult.Error);
            return;
        }

        Guid clientId = getResult.Data;

        _laserGameStatusStore.UpdateStatus(clientId, status);
        _automationEngineService.EnqueueTransition(clientId, status.Status);
    }

    public async Task Load(Guid songId)
    {
        _logger.LogInformation("Load command received for song {SongId}", songId);

        await Clients.Group(ClientGroup.Music.ToString()).SendAsync("Load", songId);
    }

    public async Task Play()
    {
        _logger.LogInformation("Play command received");

        await Clients.Group(ClientGroup.Music.ToString()).SendAsync("Play");
    }

    public async Task Pause()
    {
        _logger.LogInformation("Pause command received");

        await Clients.Group(ClientGroup.Music.ToString()).SendAsync("Pause");
    }

    public async Task Stop()
    {
        _logger.LogInformation("Stop command received");

        await Clients.Group(ClientGroup.Music.ToString()).SendAsync("Stop");
    }

    private async Task SendMusicSettingsToClientAsync(Client client)
    {
        ClientMusicSettingsDTO settings = new ClientMusicSettingsDTO { CacheLimitMb = client.MusicCacheLimitMb };

        await Clients.Caller.SendAsync("ReceiveMusicSettings", settings);
        _logger.LogInformation("Sent music settings to client {ClientId}: cache limit {CacheLimitMb}MB", client.Id, client.MusicCacheLimitMb);
    }

    private async Task SendZoneScoreboardSettingsToClientAsync(Client client)
    {
        Result<ZoneScoreboardSettings?> getResult = await _clientZoneScoreboardService.GetZoneScoreboardSettingsAsync(client.Id);

        if (!getResult.IsSuccess || getResult.Data == null)
        {
            _logger.LogError("Failed to get zone scoreboard settings for client {ClientId}: {Error}", client.Id, getResult.Error);
            return;
        }

        ZoneScoreboardSettings settings = getResult.Data;

        ZoneScoreboardSettingsDTO settingsDto = new()
        {
            PreferredDeviceMacAddress = settings.PreferredDeviceMacAddress,
            ZoneScoreboardVersion = settings.ZoneScoreboardVersion,
            SourceIp = settings.SourceIp,
            DestinationIp = settings.DestinationIp
        };

        await Clients.Caller.SendAsync("ReceiveZoneScoreboardSettings", settingsDto);
        _logger.LogInformation("Sent zone scoreboard settings to client {ClientId}", client.Id);
    }

    public async Task<Result<bool>> SendProjectionProgramInfoToClientAsync(Guid clientId)
    {
        try
        {
            Result<Client?> getResult = await _clientService.GetClientFromIdAsync(clientId);

            if (!getResult.IsSuccess || getResult.Data == null)
            {
                _logger.LogError("Failed to get client {ClientId}: {Error}", clientId, getResult.Error);
                return Result<bool>.Fail("Failed to get client.");
            }

            string? connectionId = getResult.Data!.MostRecentConnectionId;

            if (string.IsNullOrEmpty(connectionId))
            {
                _logger.LogWarning("Client {ClientId} has no recent connection ID", clientId);
                return Result<bool>.Fail("Client has no recent connection ID.");
            }

            Result<IEnumerable<ClientProjectionSettings>> result = await _clientService.GetClientProjectionSettingsAsync(clientId);

            if (!result.IsSuccess)
            {
                _logger.LogError("Failed to get projection settings for client {ConnectionId}: {Error}", Context.ConnectionId, result.Error);
                return Result<bool>.Fail("Failed to get projection settings.");
            }

            Result<IEnumerable<ClientProjectionSettingsDTO>> result1 = _clientService.ConvertIntoClientProjectionSettingsDTO(result.Data!);

            if (!result1.IsSuccess)
            {
                _logger.LogError("Failed to convert projection settings into DTO for client {ConnectionId}: {Error}", Context.ConnectionId, result.Error);
                return Result<bool>.Fail("Failed to convert projection settings into DTO.");
            }

            if (result1.Data == null || !result1.Data.Any())
            {
                return Result<bool>.Ok(true);
            }

            _logger.LogInformation("Sending projection program info to client {ConnectionId}", connectionId);

            await Clients.Clients(connectionId).SendAsync("ReceiveProjectionPrograms", result1.Data);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while sending projection program info to client {clientId}", clientId);

            return Result<bool>.Fail("An error occurred while sending projection program info to client.");
        }
    }
    public async Task ProjectionProgramCompleted()
    {
        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);

        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("ProjectionProgramCompleted: failed to resolve client ID for connection {ConnectionId}", Context.ConnectionId);
            return;
        }

        _logger.LogInformation("Client {ClientId} completed triggered projection program, restoring original settings", getClientResult.Data);

        await SendProjectionProgramInfoToClientAsync(getClientResult.Data);
    }

    public async Task ReceiveCachedSongs(Result<IEnumerable<CachedSongDTO>> cachedSongs)
    {
        await _hubEvents.RaiseReceiveCachedSongs(cachedSongs);
    }

    public async Task UpdateAvailableDmxDevices(IEnumerable<string> devices)
    {
        _logger.LogInformation("Client {ConnectionId} reported available DMX devices: {Devices}", Context.ConnectionId, devices);

        Result<Guid> getResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getResult.IsSuccess)
        {
            _logger.LogError("Failed to get client ID from connection ID {ConnectionId}: {Error}", Context.ConnectionId, getResult.Error);
            return;
        }

        Guid clientId = getResult.Data!;

        await _clientService.SetClientAvailableDmxDevicesAsync(clientId, devices);
    }

    public async Task PlayerVolumeChanged(int volume)
    {
        _logger.LogInformation("Client {ConnectionId} reported volume change: {Volume}", Context.ConnectionId, volume);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        await _playerService.SetVolume(getClientResult.Data, volume);
    }

    public async Task PlaylistChanged(Guid playlistId)
    {
        _logger.LogInformation("Client {ConnectionId} reported playlist change: {PlaylistId}", Context.ConnectionId, playlistId);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        await _playerService.SetCurrentPlaylist(getClientResult.Data, playlistId);
    }

    public async Task QueueChanged(List<Guid> queue)
    {
        _logger.LogInformation("Client {ConnectionId} reported queue change: {Queue}", Context.ConnectionId, queue);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        Guid playlistId = _playerService.GetCurrentPlaylist(getClientResult.Data)?.Id ?? Guid.Empty;

        await _playerService.SetQueue(getClientResult.Data, queue, playlistId);
    }

    public async Task UpdateDmxChannelValue(int channelAddress, byte value)
    {
        _logger.LogInformation("Client {ConnectionId} reported DMX channel update: Address {ChannelAddress}, Value {Value}", Context.ConnectionId, channelAddress, value);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        Guid clientId = getClientResult.Data;

        _dmxClientService.SetChannelValue(clientId, channelAddress, value);
    }

    public async Task SendDmxSettingsToClientAsync(Client client)
    {
        Result<ClientDmxSettingsDTO?> settingsResult = await _clientService.GetClientDmxSettings(client.Id);

        if (!settingsResult.IsSuccess)
        {
            _logger.LogError("Failed to get DMX settings for client {ClientId}: {Error}", client.Id, settingsResult.Error);
            return;
        }

        if (settingsResult.Data == null)
        {
            _logger.LogWarning("No DMX settings found for client {ClientId}", client.Id);
            return;
        }

        await Clients.Caller.SendAsync("ReceiveDmxSettings", settingsResult.Data);
    }

    public async Task UpdateAvailableNetworkInterfaces(IEnumerable<NetworkInterfaceDto> interfaces)
    {
        _logger.LogInformation("Client {ConnectionId} reported available network interfaces: {Interfaces}", Context.ConnectionId, interfaces);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        Guid clientId = getClientResult.Data;

        IEnumerable<NetworkInterfaceDto> physicalAddresses = interfaces.Select(i => new NetworkInterfaceDto
        {
            PhysicalAddress = i.PhysicalAddress,
            Name = i.Name
        });

        await _clientZoneScoreboardService.UpdateClientAvailableNetworkInterfacesAsync(clientId, physicalAddresses);
    }

    public async Task UpdateAvailableVideoDevices(IEnumerable<ClientAvailableVideoDeviceDTO> devices)
    {
        _logger.LogInformation("Client {ConnectionId} reported available video devices: {Devices}", Context.ConnectionId, devices.Select(d => d.DeviceName));

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        Guid clientId = getClientResult.Data;

        Result<bool> setResult = await _clientService.SetClientAvailableVideoDevicesAsync(clientId, devices);

        if (!setResult.IsSuccess)
        {
            _logger.LogWarning("Failed to update available video devices for client {ClientId}: {Error}", clientId, setResult.Error);
        }
    }
}
