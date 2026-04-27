using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Lanyard.Infrastructure.DTO;
using NAudio.Wave;
using Lanyard.Shared.DTO;

namespace Lanyard.Client.Controllers;

public class MusicControlHandler
{
    private readonly IMusicPlayer _musicPlayer;
    private readonly ISongCacheService _cacheService;
    private readonly ILogger<MusicControlHandler> _logger;
    private HubConnection? _connection;

    public MusicControlHandler(
        IMusicPlayer musicPlayer,
        ISongCacheService cacheService,
        ILogger<MusicControlHandler> logger)
    {
        _musicPlayer = musicPlayer;
        _cacheService = cacheService;
        _logger = logger;

        _musicPlayer.PlaybackStateChanged += OnPlaybackStateChanged;
        _musicPlayer.PlayingSongChanged += OnPlayingSongChanged;
    }

    public void Register(HubConnection connection)
    {
        _connection = connection;

        connection.On<ClientMusicSettingsDTO>("ReceiveMusicSettings", settings =>
        {
            _logger.LogInformation("Received music settings: cache limit {CacheLimitMb}MB", settings.CacheLimitMb);
            _cacheService.UpdateCacheLimit(settings.CacheLimitMb);
        });

        connection.On("Load", async (Guid songId) =>
        {
            _logger.LogInformation("Received LOAD command for song {SongId}", songId);

            Result<bool> result = await _musicPlayer.Load(songId);

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

        connection.On("PlayNext", async () =>
        {
            _logger.LogInformation("Received PLAYNEXT command");
            Result<bool> result = await _musicPlayer.PlayNext();

            if (result.IsSuccess)
            {
                _logger.LogInformation("Played next song successfully");
            }
            else
            {
                _logger.LogError("Failed to play next song: {Error}", result.Error);
            }
        });

        connection.On("PlayPrevious", async () =>
        {
            _logger.LogInformation("Received PLAYPREVIOUS command");
            Result<bool> result = await _musicPlayer.PlayPrevious();

            if (result.IsSuccess)
            {
                _logger.LogInformation("Played previous song successfully");
            }
            else
            {
                _logger.LogError("Failed to play previous song: {Error}", result.Error);
            }
        });

        connection.On("Seek", (double seconds) =>
        {
            _logger.LogInformation("Received SEEK command to {Seconds}", seconds);
            Result<bool> result = _musicPlayer.Seek(seconds);

            if (result.IsSuccess)
            {
                _logger.LogInformation("Seek command applied");
            }
            else
            {
                _logger.LogError("Failed to seek: {Error}", result.Error);
            }
        });

        connection.On("GetCachedSongs", async () =>
        {
            _logger.LogInformation("Received GETCACHEDSONGS command");
            Result<IEnumerable<CachedSongDTO>> result = _cacheService.GetCachedSongs();

            if (result.IsSuccess && result.Data != null)
            {
                _logger.LogInformation("Cached songs retrieved successfully, count: {Count}", result.Data.Count());
                await connection.InvokeAsync("ReceiveCachedSongs", Result<IEnumerable<CachedSongDTO>>.Ok(result.Data));
            }
            else
            {
                _logger.LogError("Failed to retrieve cached songs: {Error}", result.Error);
                await connection.InvokeAsync("ReceiveCachedSongs", Result<IEnumerable<CachedSongDTO>>.Fail(result.Error ?? "Unknown error"));
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
