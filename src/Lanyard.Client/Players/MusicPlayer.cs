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

    private WaveOutEvent? _player;
    private MediaFoundationReader? _reader;

    private List<Guid> SongQueue = [];
    private int QueueIndex = 0;

    public MusicPlayer(ILogger<MusicPlayer> logger, ISongCacheService cacheService)
    {
        _logger = logger;
        _cacheService = cacheService;

        _player = new WaveOutEvent();
    }

    public Result<bool> LoadPlaylist(IEnumerable<Guid> songList)
    {
        SongQueue = songList.ToList();
        QueueIndex = 0;

        return Result<bool>.Ok(true);
    }

    public async Task<Result<bool>> Load(Guid songId)
    {
        try
        {
            Stop();

            string audioSource = await _cacheService.GetAudioSourceAsync(songId);

            _logger.LogInformation("MusicPlayer: Loading audio from {Source}", audioSource);

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

    public Result<bool> Play()
    {
        if (_player == null)
        {
            return Result<bool>.Fail("Player not initialised");
        }

        if (_player.PlaybackState != PlaybackState.Playing)
        {
            _logger.LogInformation("MusicPlayer: Play");
            _player.Play();

            PlaybackStateChanged?.Invoke(PlaybackState.Playing);
            if (SongQueue.Count > 0 && QueueIndex >= 0 && QueueIndex < SongQueue.Count)
            {
                PlayingSongChanged?.Invoke(SongQueue[QueueIndex]);
                PreCacheNext();
            }
        }

        return Result<bool>.Ok(true);
    }

    public Result<bool> Pause()
    {
        if (_player == null)
        {
            return Result<bool>.Fail("Player not initialised");
        }

        if (_player.PlaybackState == PlaybackState.Playing)
        {
            _logger.LogInformation("MusicPlayer: Pause");
            _player.Pause();

            PlaybackStateChanged?.Invoke(PlaybackState.Paused);
        }

        return Result<bool>.Ok(true);
    }

    public void Stop()
    {
        _logger.LogInformation("MusicPlayer: Stop");

        PlaybackStateChanged?.Invoke(PlaybackState.Stopped);

        _player?.Stop();

        _reader?.Dispose();
        _reader = null;
    }

    public async Task<Result<bool>> PlayNext()
    {
        try
        {
            if (SongQueue.Count == 0)
            {
                return Result<bool>.Fail("No songs are loaded.");
            }

            QueueIndex = (QueueIndex + 1) % SongQueue.Count;
            Result<bool> result = await Load(SongQueue[QueueIndex]);
            if (!result.IsSuccess)
            {
                return result;
            }

            Play();

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
            if (SongQueue.Count == 0)
            {
                return Result<bool>.Fail("No songs are loaded.");
            }

            QueueIndex = QueueIndex - 1 < 0 ? SongQueue.Count - 1 : QueueIndex - 1;

            Result<bool> result = await Load(SongQueue[QueueIndex]);
            if (!result.IsSuccess)
            {
                return result;
            }

            Play();

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
            if (SongQueue.Count == 0 || QueueIndex < 0 || QueueIndex >= SongQueue.Count)
            {
                return Result<Guid>.Fail("No current song.");
            }

            return Result<Guid>.Ok(SongQueue[QueueIndex]);
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
        if (SongQueue.Count <= 1) return;

        int nextIndex = (QueueIndex + 1) % SongQueue.Count;
        _cacheService.PreCacheInBackground(SongQueue[nextIndex]);
    }

    public void Dispose()
    {
        Stop();
        _player?.Dispose();
    }
}
