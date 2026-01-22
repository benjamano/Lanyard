using Lanyard.Application.Services;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using System.Collections.Concurrent;

namespace Lanyard.Application.SignalR;

public class SignalRControlHub(ILogger<SignalRControlHub> logger, MusicPlayerService playerService, IClientService clientService) : Hub
{
    private readonly ILogger<SignalRControlHub> _logger = logger;

    private readonly MusicPlayerService _playerService = playerService;
    private readonly IClientService _clientService = clientService;

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
                CreateDate = DateTime.Now,
                LastLogin = DateTime.Now
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

            client.LastUpdateDate = DateTime.Now;
            client.MostRecentConnectionId = Context.ConnectionId;
            client.MostRecentIpAddress = clientIp;
            client.LastLogin = DateTime.Now;

            Result<Client?> updateResult = await _clientService.UpdateClientAsync(client);

            if (!updateResult.IsSuccess)
            {
                _logger.LogError("Failed to update client entry for ID {ClientId}: {Error}", clientId, updateResult.Error);

                Context.Abort();
                return;
            }
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

        _connections.TryRemove(Context.ConnectionId, out _);

        await base.OnDisconnectedAsync(exception);
    }

    public async Task PlaybackStateChanged(PlaybackState state)
    {
        _logger.LogInformation("Client {ConnectionId} reported playback state: {State}", Context.ConnectionId, state);
        
        _playerService.UpdatePlaybackState(state);
        
        await Clients.Group(ClientGroup.Music.ToString()).SendAsync("PlaybackStateChanged", state);
    }

    public async Task CurrentPlayingSongChanged(Guid song)
    {
        _logger.LogInformation("Client {ConnectionId} reported new song: {Song}", Context.ConnectionId, song);

        await Clients.Group(ClientGroup.Music.ToString()).SendAsync("CurrentPlayingSongChanged", song);
    }

    public async Task UpdateAvailableScreens(IEnumerable<ClientAvailableScreenDTO> screens)
    {
        _logger.LogInformation("Client {ConnectionId} reported available screens: {Screens}", Context.ConnectionId, screens);

        Result<Guid> getResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getResult.IsSuccess)
        {
            _logger.LogError("Failed to get client ID from connection ID {ConnectionId}: {Error}", Context.ConnectionId, getResult.Error);
            return;
        }

        Guid clientId = getResult.Data!;

        await _clientService.SetClientAvailableScreensAsync(clientId, screens);
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
}