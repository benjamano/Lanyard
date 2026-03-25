using Lanyard.Infrastructure.DTO;
using NAudio.Wave;

public interface IMusicPlayer
{
    event Action<PlaybackState>? PlaybackStateChanged;
    event Action<Guid>? PlayingSongChanged;

    Result<bool> Play();
    Result<bool> Pause();
    Task<Result<bool>> PlayNext();
    Task<Result<bool>> PlayPrevious();
    Result<bool> Seek(double seconds);
    Result<bool> LoadPlaylist(IEnumerable<Guid> songList);

    Result<PlaybackState> GetPlaybackStatus();
    Result<Guid> GetCurrentSongId();

    Task<Result<bool>> Load(Guid songId);
    void Stop();
}
