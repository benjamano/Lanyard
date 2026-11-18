using Lanyard.Infrastructure.DTO;
using NAudio.Wave;

public interface IMusicPlayer
{
    event Action<PlaybackState>? PlaybackStateChanged;
    event Action<Guid>? PlayingSongChanged;
    event Action<int>? PlayerVolumeChanged;
    event Action<Guid>? PlaylistChanged;
    event Action<List<Guid>?>? QueueChanged;

    /// <summary>
    /// Raised when a track finishes on its own. The server owns the queue, repeat and shuffle
    /// state, so it decides what plays next — the player only reports that the track ended.
    /// </summary>
    event Action? SongEnded;

    Task<Result<bool>> Play();
    Task<Result<bool>> Pause();
    Task<Result<bool>> PlayNext();
    Task<Result<bool>> PlayPrevious();
    Result<bool> Seek(double seconds);
    /// <summary>Replaces the queue and loads its first song. Cancelling abandons the download wait.</summary>
    Task<Result<bool>> LoadPlaylist(Dictionary<Guid, Guid> songList, CancellationToken cancellationToken = default);

    Result<PlaybackState> GetPlaybackStatus();
    Result<Guid> GetCurrentSongId();

    /// <summary>
    /// Ground-truth playback position from the audio reader, in seconds. Fails when
    /// no song is loaded. Reported to the server so beat-synced features can align
    /// to the real audio clock instead of a wall-clock estimate.
    /// </summary>
    Result<double> GetPositionSeconds();

    Task<Result<bool>> Load(Guid songId, Guid playlistId);

    /// <summary>Load, abandoned (throwing OperationCanceledException) if a newer load replaces it.</summary>
    Task<Result<bool>> Load(Guid songId, Guid playlistId, CancellationToken cancellationToken);
    void Stop(bool notify = true);

    Task<Result<bool>> SetVolumeAsync(int volume);
    Task<Result<int>> GetVolumeAsync();

    Task UpdateServerPlaybackStatus();
    Task UpdateServerCurrentPlayingSong();
    Task SendServerCurrentVolume();
    Task SendServerCurrentPlaylist();
    Task SendServerCurrentQueue();
}
