using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using System.Net.Http;

public class MusicPlayer : IMusicPlayer, IDisposable
{
    private readonly ILogger<MusicPlayer> _logger;
    private readonly ISongCacheService _cacheService;
    private readonly MusicGain _gain;

    public event Action<PlaybackState>? PlaybackStateChanged;
    public event Action<Guid>? PlayingSongChanged;
    public event Action<int>? PlayerVolumeChanged;
    public event Action<Guid>? PlaylistChanged;
    public event Action<List<Guid>?>? QueueChanged;
    public event Action? SongEnded;

    private WaveOutEvent? _player;
    private MediaFoundationReader? _reader;

    // The queue the server sent, song -> playlist, in play order. Replaced whole, never edited in
    // place, so the reconnect report and pre-caching can read it from other threads.
    private Dictionary<Guid, Guid> SongAndPlaylistQueue = [];
    private Guid QueueIndex = Guid.Empty;

    // Guards swapping the audio reader, which the position timer reads every second.
    private readonly object _readerLock = new();

    public MusicPlayer(ILogger<MusicPlayer> logger, ISongCacheService cacheService, MusicGain gain)
    {
        _logger = logger;
        _cacheService = cacheService;
        _gain = gain;

        _player = new WaveOutEvent();
        _player.PlaybackStopped += OnPlaybackStopped;

        // Volume lives on the samples now (see MusicGain). Older versions set it on the device,
        // and Windows may have kept that level for this app, so put the device back to full.
        _player.Volume = 1f;
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
            // gets here, so a null reader means this was an explicit stop, not a natural end.
            if (reader is null)
            {
                _logger.LogInformation("MusicPlayer: Stop was explicit, not treating as track end");
                return;
            }

            // MediaFoundationReader.Length is an estimate and can run slightly longer than what
            // actually gets played for some files (e.g. VBR MP3), so Position rarely reaches it
            // exactly on a natural end. A short tolerance avoids missing genuine track-end events.
            TimeSpan remaining = reader.TotalTime - reader.CurrentTime;
            if (remaining > TimeSpan.FromMilliseconds(750))
            {
                _logger.LogInformation(
                    "MusicPlayer: Playback stopped with {Remaining}ms remaining - not treating as track end",
                    remaining.TotalMilliseconds);
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

    public async Task<Result<bool>> LoadPlaylist(Dictionary<Guid, Guid> songList, CancellationToken cancellationToken = default)
    {
        // An empty playlist has nothing to load; keep whatever is queued now rather than
        // swapping in an empty queue and looking up a song that isn't there.
        if (songList.Count == 0)
        {
            return Result<bool>.Fail("Playlist has no songs");
        }

        SongAndPlaylistQueue = new Dictionary<Guid, Guid>(songList);
        QueueIndex = songList.Keys.FirstOrDefault();

        return await Load(QueueIndex, SongAndPlaylistQueue[QueueIndex], cancellationToken);
    }

    public Task<Result<bool>> Load(Guid songId, Guid playlistId) => Load(songId, playlistId, CancellationToken.None);

    public async Task<Result<bool>> Load(Guid songId, Guid playlistId, CancellationToken cancellationToken)
    {
        try
        {
            Stop(false);

            _cacheService.SetActiveSong(songId);

            string audioSource = await _cacheService.GetAudioSourceAsync(songId, cancellationToken);

            // A newer load arrived while this one was downloading; it decides what plays.
            cancellationToken.ThrowIfCancellationRequested();

            // audioSource may be a direct API URL carrying the client's shared secret in the
            // query string (see SongCacheService.BuildApiUrl) - strip it before logging so the
            // secret never ends up in the client's log file.
            int queryIndex = audioSource.IndexOf('?');
            string logSafeSource = queryIndex >= 0 ? audioSource[..queryIndex] : audioSource;
            _logger.LogInformation("MusicPlayer: Loading audio from {Source}", logSafeSource);

            // Only songs the server queued belong in the queue. A song played offline (no playlist)
            // used to be added too, so the queue grew all day and was sent back on reconnect.
            if (playlistId != Guid.Empty && !SongAndPlaylistQueue.ContainsKey(songId))
            {
                SongAndPlaylistQueue = new Dictionary<Guid, Guid>(SongAndPlaylistQueue) { [songId] = playlistId };
            }

            QueueIndex = songId;

            MediaFoundationReader reader = new(audioSource);

            lock (_readerLock)
            {
                _reader = reader;
                _player!.Init(new GainSampleProvider(_reader.ToSampleProvider(), _gain));
            }

            return Result<bool>.Ok(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("MusicPlayer: Load of {SongId} replaced by a newer one", songId);
            throw;
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

        lock (_readerLock)
        {
            _reader?.Dispose();
            _reader = null;
        }

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

    public Result<double> GetPositionSeconds()
    {
        try
        {
            lock (_readerLock)
            {
                if (_reader is null)
                {
                    return Result<double>.Fail("No song loaded.");
                }

                return Result<double>.Ok(_reader.CurrentTime.TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicPlayer: Failed to get playback position");
            return Result<double>.Fail($"Failed to get playback position: {ex.Message}");
        }
    }

    public Result<bool> Seek(double seconds)
    {
        lock (_readerLock)
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
            _gain.SetBaseVolume(volume);

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
            // The volume the server set, not the ducked level, so a clip never moves the slider.
            return Result<int>.Ok(_gain.BaseVolume);
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
