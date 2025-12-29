using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

public class MusicPlayer : IMusicPlayer, IDisposable
{
    private readonly ILogger<MusicPlayer> _logger;

    public event Action<PlaybackState>? PlaybackStateChanged;
    public event Action<Guid>? PlayingSongChanged;
    
    private WaveOutEvent? _player;
    private MediaFoundationReader? _reader;

    private List<Guid> SongQueue = [];
    private int QueueIndex = 0;

    public MusicPlayer(ILogger<MusicPlayer> logger, HttpClient httpClient)
    {
        _logger = logger;

        _player = new WaveOutEvent();
    }

    public Result<bool> LoadPlaylist(IEnumerable<Guid> songList)
    {
        SongQueue.AddRange(songList);

        return Result<bool>.Ok(true);
    }

    public Result<bool> Load(Guid songId)
    {
        try
        {
            Stop();

            string audioUrl = $"{Environment.GetEnvironmentVariable("API_SERVER_URL")}/music/audio/{songId}";

            _logger.LogInformation("MusicPlayer: Loading audio from {Url}", audioUrl);

            _reader = new MediaFoundationReader(audioUrl);
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
            PlayingSongChanged?.Invoke(SongQueue[QueueIndex]);
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

    public Result<bool> PlayNext()
    {
        try
        {
            QueueIndex++;

            Result<bool> result = Load(SongQueue[QueueIndex]);
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

    public Result<bool> PlayPrevious()
    {
        try
        {
            QueueIndex--;

            Result<bool> result = Load(SongQueue[QueueIndex]);
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
            return Result<Guid>.Ok(SongQueue[QueueIndex]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicPlayer: Failed to get current song id");
            return Result<Guid>.Fail($"Failed to get current song id: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _player?.Dispose();
    }
}
