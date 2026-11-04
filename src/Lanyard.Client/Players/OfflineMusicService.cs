using Lanyard.Client.SignalR;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

public interface IOfflineMusicService
{
    /// <summary>
    /// Keeps the music going from the song cache while the server can't pick the next track. Does
    /// nothing if music wasn't playing when the connection dropped, so a quiet venue stays quiet.
    /// </summary>
    Task EnterOfflineModeAsync();

    /// <summary>
    /// Hands track choice back to the server. The current song finishes, and its end is reported
    /// to the server as usual, which then carries on with its own playlist.
    /// </summary>
    void ExitOfflineMode();
}

/// <summary>
/// The server owns the music queue (repeat, shuffle, what's next), so without it music stops after
/// the current track. While the kiosk is offline this shuffles through the songs already in the
/// song cache instead.
/// </summary>
public class OfflineMusicService : IOfflineMusicService
{
    private readonly ILogger<OfflineMusicService> _logger;
    private readonly IMusicPlayer _musicPlayer;
    private readonly ISongCacheService _songCache;
    private readonly ISignalRClient _signalRClient;

    private readonly Queue<Guid> _shuffled = new();
    private volatile bool _active;

    // A track can end in the grace period before the kiosk switches over; that end was never
    // reported to the server, so the music should pick up again once offline.
    private volatile bool _songEndedWhileDisconnected;

    public OfflineMusicService(ILogger<OfflineMusicService> logger, IMusicPlayer musicPlayer, ISongCacheService songCache, ISignalRClient signalRClient)
    {
        _logger = logger;
        _musicPlayer = musicPlayer;
        _songCache = songCache;
        _signalRClient = signalRClient;

        _musicPlayer.SongEnded += OnSongEnded;
        _signalRClient.ConnectionStateChanged += connected =>
        {
            if (connected)
            {
                _songEndedWhileDisconnected = false;
            }
        };
    }

    public async Task EnterOfflineModeAsync()
    {
        Result<PlaybackState> state = _musicPlayer.GetPlaybackStatus();
        bool wasPlaying = state.IsSuccess && state.Data == PlaybackState.Playing;

        if (!wasPlaying && !_songEndedWhileDisconnected)
        {
            _logger.LogInformation("Offline music: music wasn't playing, staying quiet");
            return;
        }

        _active = true;
        _logger.LogInformation("Offline music: shuffling cached songs until the server is back");

        if (!wasPlaying)
        {
            await PlayNextCachedSongAsync();
        }
    }

    public void ExitOfflineMode()
    {
        if (_active)
        {
            _logger.LogInformation("Offline music: handing track choice back to the server");
        }

        _active = false;
        _shuffled.Clear();
    }

    private async void OnSongEnded()
    {
        try
        {
            if (_active)
            {
                await PlayNextCachedSongAsync();
            }
            else if (!_signalRClient.IsConnected)
            {
                _songEndedWhileDisconnected = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Offline music: failed to start the next song");
        }
    }

    private async Task PlayNextCachedSongAsync()
    {
        Guid current = _musicPlayer.GetCurrentSongId().Data;

        if (_shuffled.Count == 0)
        {
            Refill(current);
        }

        // A song can be evicted from the cache between refills; skip any that went.
        while (_shuffled.TryDequeue(out Guid songId))
        {
            if (!_active)
            {
                return;
            }

            // Load would otherwise stream a song that isn't cached, which can't work offline.
            bool stillCached = _songCache.GetCachedSongs().Data?.Any(x => x.Id == songId) == true;

            if (!stillCached)
            {
                continue;
            }

            Result<bool> loaded = await _musicPlayer.Load(songId, Guid.Empty);

            if (!loaded.IsSuccess)
            {
                continue;
            }

            await _musicPlayer.Play();
            _logger.LogInformation("Offline music: playing cached song {SongId}", songId);
            return;
        }

        _logger.LogInformation("Offline music: no cached songs to play");
    }

    private void Refill(Guid current)
    {
        List<Guid> songs = [.. (_songCache.GetCachedSongs().Data ?? []).Select(x => x.Id)];

        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(songs));

        // Don't play the song that just finished straight away again, unless it's the only one.
        foreach (Guid song in songs.Where(x => x != current || songs.Count == 1))
        {
            _shuffled.Enqueue(song);
        }
    }
}
