using Lanyard.Infrastructure.DTO;
using NAudio.Wave;

public interface IMusicPlayer
{
    event Action<PlaybackState>? PlaybackStateChanged;
    event Action<Guid>? PlayingSongChanged;

    Result<bool> Play();
    Result<bool> Pause();
    Result<bool> PlayNext();
    Result<bool> PlayPrevious();
    Result<bool> LoadPlaylist(IEnumerable<Guid> songList);

    Result<PlaybackState> GetPlaybackStatus();
    Result<Guid> GetCurrentSongId();

    Result<bool> Load(Guid songId);
    void Stop();
}