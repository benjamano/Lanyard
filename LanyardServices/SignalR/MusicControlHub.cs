using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Lanyard.Application.Services;

namespace Lanyard.Application.SignalR;

/// <summary>
/// SignalR Hub for music playback control.
/// Receives commands from clients and broadcasts state changes to the Music group.
/// </summary>
public class MusicControlHub : Hub
{
    private readonly ILogger<MusicControlHub> _logger;
    private readonly MusicPlayerService _playerService;
    private const string MusicGroup = "Music";

    public MusicControlHub(
        ILogger<MusicControlHub> logger,
        MusicPlayerService playerService)
    {
        _logger = logger;
        _playerService = playerService;
    }

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, MusicGroup);
        _logger.LogInformation("Client {ConnectionId} connected and added to Music group", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, MusicGroup);
        _logger.LogInformation("Client {ConnectionId} disconnected from Music group", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task PlaybackStateChanged(PlaybackState state)
    {
        _logger.LogInformation("Client {ConnectionId} reported playback state: {State}", Context.ConnectionId, state);
        
        _playerService.UpdatePlaybackState(state);
        
        await Clients.Group(MusicGroup).SendAsync("PlaybackStateChanged", state);
    }

    public async Task CurrentPlayingSongChanged(Guid song)
    {
        _logger.LogInformation("Client {ConnectionId} reported new song: {Song}", Context.ConnectionId, song);

        await Clients.Group(MusicGroup).SendAsync("CurrentPlayingSongChanged", song);
    }

    public async Task Load(Guid songId)
    {
        _logger.LogInformation("Load command received for song {SongId}", songId);
        
        await Clients.Group(MusicGroup).SendAsync("Load", songId);
    }

    public async Task Play()
    {
        _logger.LogInformation("Play command received");
        
        await Clients.Group(MusicGroup).SendAsync("Play");
    }

    public async Task Pause()
    {
        _logger.LogInformation("Pause command received");
        
        await Clients.Group(MusicGroup).SendAsync("Pause");
    }

    public async Task Stop()
    {
        _logger.LogInformation("Stop command received");
        
        await Clients.Group(MusicGroup).SendAsync("Stop");
    }
}