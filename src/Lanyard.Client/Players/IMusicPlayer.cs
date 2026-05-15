using Lanyard.Infrastructure.DTO;
using NAudio.Wave;

public interface IMusicPlayer
{
    event Action<PlaybackState>? PlaybackStateChanged;
    event Action<Guid>? PlayingSongChanged;
    event Action<int>? PlayerVolumeChanged;
    event Action<Guid>? PlaylistChanged;

    Task<Result<bool>> Play();
    Task<Result<bool>> Pause();
    Task<Result<bool>> PlayNext();
    Task<Result<bool>> PlayPrevious();
    Result<bool> Seek(double seconds);
    Task<Result<bool>> LoadPlaylist(Dictionary<Guid, Guid> songList);

    Result<PlaybackState> GetPlaybackStatus();
    Result<Guid> GetCurrentSongId();

    Task<Result<bool>> Load(Guid songId, Guid playlistId);
    void Stop(bool notify = true);

    Task<Result<bool>> SetVolumeAsync(int volume);
    Task<Result<int>> GetVolumeAsync();

    Task UpdateServerPlaybackStatus();
    Task UpdateServerCurrentPlayingSong();
    Task SendServerCurrentVolume();
    Task SendServerCurrentPlaylist();
}
