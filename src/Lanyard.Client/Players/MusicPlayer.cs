using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using System.Net.Http;

public class MusicPlayer : IMusicPlayer, IDisposable
{
    private readonly ILogger<MusicPlayer> _logger;
    private readonly ISongCacheService _cacheService;

    public event Action<PlaybackState>? PlaybackStateChanged;
    public event Action<Guid>? PlayingSongChanged;
    public event Action<int>? PlayerVolumeChanged;
    public event Action<Guid>? PlaylistChanged;
    public event Action<List<Guid>?>? QueueChanged;
    public event Action? SongEnded;

    private WaveOutEvent? _player;
    private MediaFoundationReader? _reader;

    private Dictionary<Guid, Guid> SongAndPlaylistQueue = [];
    private Guid QueueIndex = Guid.Empty;

    public MusicPlayer(ILogger<MusicPlayer> logger, ISongCacheService cacheService)
    {
        _logger = logger;
        _cacheService = cacheService;

        _player = new WaveOutEvent();
        _player.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>
    /// Fires when the NAudio playback thread exits — either because Stop was called or because
    /// the stream ran out. Without this the server is never told a track finished, so it keeps
    /// reporting Playing and estimating the position past the end of the song.
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _logger.LogError(e.Exception, "MusicPlayer: Playback stopped unexpectedly");
        }

        try
        {
            MediaFoundationReader? reader = _reader;

            // Stop (and the Stop inside Load) disposes the reader before the playback thread
            // gets here and reports the state itself, so a live reader that has consumed its
            // whole stream is a track that ended on its own.
            if (reader is null || reader.Position < reader.Length)
            {
                return;
            }

            _logger.LogInformation("MusicPlayer: Reached end of track {SongId}", QueueIndex);

            // Rewind rather than dispose, so a later Play (or a Seek issued while stopped)
            // starts from a sane position instead of replaying the last moment of the track.
            reader.CurrentTime = TimeSpan.Zero;

            UpdateServerPlaybackStatus();

            SongEnded?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicPlayer: Failed to handle end of playback");
        }
    }

    public async Task<Result<bool>> LoadPlaylist(Dictionary<Guid, Guid> songList)
    {
        SongAndPlaylistQueue = songList;
        QueueIndex = songList.Keys.FirstOrDefault();

        await Load(QueueIndex, SongAndPlaylistQueue[QueueIndex]);

        return Result<bool>.Ok(true);
    }

    public async Task<Result<bool>> Load(Guid songId, Guid playlistId)
    {
        try
        {
            Stop(false);

            string audioSource = await _cacheService.GetAudioSourceAsync(songId);

            _logger.LogInformation("MusicPlayer: Loading audio from {Source}", audioSource);

            SongAndPlaylistQueue[songId] = playlistId;

            QueueIndex = songId;

            _reader = new MediaFoundationReader(audioSource);
            _player!.Init(_reader);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicPlayer: Failed to load song {SongId}", songId);
            return Result<bool>.Fail($"Failed to load audio stream: {ex.Message}");
        }
    }

    public async Task<Result<bool>> Play()
    {
        if (_player == null)
        {
            return Result<bool>.Fail("Player not initialised");
        }

        if (_player.PlaybackState == PlaybackState.Playing)
        {
            return Result<bool>.Ok(true);
        }

        // Resume from a pause without reloading, otherwise the track restarts from the beginning.
        if (_player.PlaybackState == PlaybackState.Paused && _reader != null)
        {
            _logger.LogInformation("MusicPlayer: Resume");
            _player.Play();

            await UpdateServerPlaybackStatus();

            return Result<bool>.Ok(true);
        }

        // Stopped: reuse an already-loaded reader so playback picks up from wherever it sits
        // (start of a finished track, or a position the server seeked to while stopped).
        // Reloading here would throw that position away and always restart from zero.
        if (_reader == null)
        {
            if (QueueIndex == Guid.Empty || !SongAndPlaylistQueue.TryGetValue(QueueIndex, out Guid playlistId))
            {
                return Result<bool>.Fail("No song loaded.");
            }

            Result<bool> loadResult = await Load(QueueIndex, playlistId);

            if (!loadResult.IsSuccess)
            {
                return loadResult;
            }
        }

        _logger.LogInformation("MusicPlayer: Play");
        _player.Play();

        await UpdateServerPlaybackStatus();

        if (SongAndPlaylistQueue.Count > 0 && QueueIndex != Guid.Empty)
        {
            await UpdateServerCurrentPlayingSong();

            PreCacheNext();
        }

        return Result<bool>.Ok(true);
    }

    public async Task<Result<bool>> Pause()
    {
        if (_player == null)
        {
            return Result<bool>.Fail("Player not initialised");
        }

        if (_player.PlaybackState == PlaybackState.Playing)
        {
            _logger.LogInformation("MusicPlayer: Pause");
            _player.Pause();

            await UpdateServerPlaybackStatus();
        }

        return Result<bool>.Ok(true);
    }

    public void Stop(bool notify = true)
    {
        _logger.LogInformation("MusicPlayer: Stop");

        _player?.Stop();

        _reader?.Dispose();
        _reader = null;

        // Report after stopping — reading the state first sends the server the outgoing
        // state (Playing), leaving it convinced the track is still running.
        if (notify)
        {
            UpdateServerPlaybackStatus();
        }
    }

    public async Task<Result<bool>> PlayNext()
    {
        try
        {
            if (SongAndPlaylistQueue.Count == 0)
            {
                return Result<bool>.Fail("No songs are loaded.");
            }

            List<Guid> keys = SongAndPlaylistQueue.Keys.ToList();
            int currentIdx = keys.IndexOf(QueueIndex);
            int nextIdx = (currentIdx + 1) % keys.Count;
            Guid nextSongId = keys[nextIdx];
            Guid nextPlaylistId = SongAndPlaylistQueue[nextSongId];

            Result<bool> result = await Load(nextSongId, nextPlaylistId);
            if (!result.IsSuccess)
            {
                return result;
            }

            await Play();

            return Result<bool>.Ok(true);
        }
        catch(Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> PlayPrevious()
    {
        try
        {
            if (SongAndPlaylistQueue.Count == 0)
            {
                return Result<bool>.Fail("No songs are loaded.");
            }

            List<Guid> keys = SongAndPlaylistQueue.Keys.ToList();
            int currentIdx = keys.IndexOf(QueueIndex);
            int prevIdx = currentIdx - 1 < 0 ? keys.Count - 1 : currentIdx - 1;
            Guid prevSongId = keys[prevIdx];
            Guid prevPlaylistId = SongAndPlaylistQueue[prevSongId];

            Result<bool> result = await Load(prevSongId, prevPlaylistId);
            if (!result.IsSuccess)
            {
                return result;
            }

            await Play();

            return Result<bool>.Ok(true);
        }
        catch(Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public Result<PlaybackState> GetPlaybackStatus()
    {
        try
        {
            PlaybackState state;

            if (_player == null)
            {
                state = PlaybackState.Stopped;
                
                return Result<PlaybackState>.Fail("Player not initialised");
            }

            state = _player.PlaybackState;

            return Result<PlaybackState>.Ok(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicPlayer: Failed to get playback status");
            return Result<PlaybackState>.Fail($"Failed to get playback status: {ex.Message}");
        }
    }

    public Result<Guid> GetCurrentSongId()
    {
        try
        {
            if (SongAndPlaylistQueue.Count == 0 || QueueIndex == Guid.Empty || !SongAndPlaylistQueue.ContainsKey(QueueIndex))
            {
                return Result<Guid>.Fail("No current song.");
            }

            return Result<Guid>.Ok(QueueIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicPlayer: Failed to get current song id");
            return Result<Guid>.Fail($"Failed to get current song id: {ex.Message}");
        }
    }

    public Result<bool> Seek(double seconds)
    {
        if (_reader is null)
        {
            return Result<bool>.Fail("No song loaded.");
        }

        double safeSeconds = Math.Max(0, seconds);
        TimeSpan targetTime = TimeSpan.FromSeconds(safeSeconds);

        if (_reader.TotalTime > TimeSpan.Zero && targetTime > _reader.TotalTime)
        {
            targetTime = _reader.TotalTime;
        }

        _reader.CurrentTime = targetTime;
        return Result<bool>.Ok(true);
    }

    private void PreCacheNext()
    {
        if (SongAndPlaylistQueue.Count <= 1) return;

        List<Guid> keys = SongAndPlaylistQueue.Keys.ToList();
        int currentIdx = keys.IndexOf(QueueIndex);
        int nextIdx = (currentIdx + 1) % keys.Count;
        _cacheService.PreCacheInBackground(keys[nextIdx]);
    }

    public async Task<Result<bool>> SetVolumeAsync(int volume)
    {
        if (_player == null)
        {
            return Result<bool>.Fail("Player not initialised");
        }

        try
        {
            float volumeFloat = Math.Clamp(volume / 100f, 0f, 1f);

            _player.Volume = volumeFloat;

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicPlayer: Failed to set volume");

            return Result<bool>.Fail($"Failed to set volume: {ex.Message}");
        }
    }

    public async Task<Result<int>> GetVolumeAsync()
    {
        if (_player == null)
        {
            return Result<int>.Fail("Player not initialised");
        }

        try
        {
            int volumeInt = (int)Math.Round(_player.Volume * 100);
            return Result<int>.Ok(volumeInt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicPlayer: Failed to get volume");

            return Result<int>.Fail($"Failed to get volume: {ex.Message}");
        }
    }

    public Task UpdateServerPlaybackStatus()
    {
        PlaybackState state = GetPlaybackStatus().IsSuccess ? GetPlaybackStatus().Data! : PlaybackState.Stopped;

        PlaybackStateChanged?.Invoke(state);
        
        return Task.CompletedTask;
    }

    public Task UpdateServerCurrentPlayingSong()
    {
        Guid? songId = GetCurrentSongId().IsSuccess ? GetCurrentSongId().Data : null;

        PlayingSongChanged?.Invoke(songId ?? Guid.Empty);

        return Task.CompletedTask;
    }

    public Task SendServerCurrentVolume()
    {
        int volume = GetVolumeAsync().Result.Data;

        PlayerVolumeChanged?.Invoke(volume);

        return Task.CompletedTask;
    }

    public Task SendServerCurrentPlaylist()
    {
        Guid? currentPlaylistId = QueueIndex != Guid.Empty && SongAndPlaylistQueue.ContainsKey(QueueIndex) ? SongAndPlaylistQueue[QueueIndex] : null;

        PlaylistChanged?.Invoke(currentPlaylistId ?? Guid.Empty);

        return Task.CompletedTask;
    }

    public async Task SendServerCurrentQueue()
    {
        if (SongAndPlaylistQueue.Count == 0)
        {
            _logger.LogInformation("No songs in queue to send to server.");
            return;
        }

        List<Guid> songIds = SongAndPlaylistQueue.Keys.ToList();

        QueueChanged?.Invoke(songIds);

        _logger.LogInformation("Sending current song queue to server: {SongIds}", string.Join(", ", songIds));
    }

    public void Dispose()
    {
        Stop();
        _player?.Dispose();
    }
}
