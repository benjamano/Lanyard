using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Lanyard.Infrastructure.DTO;
using NAudio.Wave;

namespace Lanyard.Client.Controllers;

/// <summary>
/// Handles SignalR communication between the server and the headless music player.
/// Receives playback commands from the server and sends state updates back.
/// </summary>
public class MusicControlHandler
{
    private readonly IMusicPlayer _musicPlayer;
    private readonly ILogger<MusicControlHandler> _logger;
    private HubConnection? _connection;

    public MusicControlHandler(
        IMusicPlayer musicPlayer,
        ILogger<MusicControlHandler> logger)
    {
        _musicPlayer = musicPlayer;
        _logger = logger;

        _musicPlayer.PlaybackStateChanged += OnPlaybackStateChanged;
        _musicPlayer.PlayingSongChanged += OnPlayingSongChanged;
    }

    /// <summary>
    /// Registers this handler with a SignalR hub connection.
    /// Sets up event handlers for server commands.
    /// </summary>
    public void Register(HubConnection connection)
    {
        _connection = connection;

        connection.On("Load", (Guid songId) =>
        {
            _logger.LogInformation("Received LOAD command for song {SongId}", songId);
            
            Result<bool> result = _musicPlayer.Load(songId);

            if (result.IsSuccess)
            {
                _logger.LogInformation("Song loaded successfully: {SongId}", songId);
            }
            else
            {
                _logger.LogError("Failed to load song {SongId}: {Error}", songId, result.Error);
            }
        });

        connection.On("Play", () =>
        {
            _logger.LogInformation("Received PLAY command");
            
            Result<bool> result = _musicPlayer.Play();

            if (result.IsSuccess)
            {
                _logger.LogInformation("Playback started successfully");
            }
            else
            {
                _logger.LogError("Failed to start playback: {Error}", result.Error);
            }
        });

        connection.On("Pause", () =>
        {
            _logger.LogInformation("Received PAUSE command");
            Result<bool> result = _musicPlayer.Pause();

            if (result.IsSuccess)
            {
                _logger.LogInformation("Playback paused successfully");
            }
            else
            {
                _logger.LogError("Failed to pause playback: {Error}", result.Error);
            }
        });

        connection.On("Stop", () =>
        {
            _logger.LogInformation("Received STOP command");
            _musicPlayer.Stop();
        });

        connection.On("LoadPlaylist", (IEnumerable<Guid> songList) =>
        {
            _logger.LogInformation("Received LOADPLAYLIST command");
            Result<bool> result = _musicPlayer.LoadPlaylist(songList);

            if (result.IsSuccess)
            {
                _logger.LogInformation("Playlist loaded successfully");
            }
            else
            {
                _logger.LogError("Failed to load playlist: {Error}", result.Error);
            }
        });

        connection.On("PlayNext", () =>
        {
            _logger.LogInformation("Received PLAYNEXT command");
            Result<bool> result = _musicPlayer.PlayNext();

            if (result.IsSuccess)
            {
                _logger.LogInformation("Played next song successfully");
            }
            else
            {
                _logger.LogError("Failed to play next song: {Error}", result.Error);
            }
        });

        connection.On("PlayPrevious", () =>
        {
            _logger.LogInformation("Received PLAYPREVIOUS command");
            Result<bool> result = _musicPlayer.PlayPrevious();

            if (result.IsSuccess)
            {
                _logger.LogInformation("Played previous song successfully");
            }
            else
            {
                _logger.LogError("Failed to play previous song: {Error}", result.Error);
            }
        });
    }

    /// <summary>
    /// Sends playback state changes to the server via SignalR.
    /// Called when the music player's state changes.
    /// </summary>
    private async void OnPlaybackStateChanged(PlaybackState state)
    {
        if (_connection == null || _connection.State != HubConnectionState.Connected)
        {
            _logger.LogWarning("Cannot send playback state - connection not established");
            return;
        }

        try
        {
            _logger.LogInformation("Sending playback state change: {State}", state);
            await _connection.InvokeAsync("PlaybackStateChanged", state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send playback state to server");
        }
    }

    private async void OnPlayingSongChanged(Guid songId)
    {
        if (_connection == null || _connection.State != HubConnectionState.Connected)
        {
            _logger.LogWarning("Cannot send playback state - connection not established");
            return;
        }

        try
        {
            _logger.LogInformation("Sending playing song change: {songId}", songId);
            await _connection.InvokeAsync("CurrentPlayingSongChanged", songId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send playing song change to server");
        }
    }
}