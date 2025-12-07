using LanyardData.Models;
using NAudio.Wave;

namespace LanyardAPI.Services;

public class MusicPlayer : IDisposable
{
    private WaveOutEvent? _player = new();
    private AudioFileReader? _audioFile;
    private bool _playerInitialized = false;
    private List<Song> _songQueue = [];
    private int _songIndex = 0;

    public Song? CurrentSong => _songQueue.Count > 0 && _songIndex < _songQueue.Count ? _songQueue[_songIndex] : null;
    public Playlist? CurrentPlaylist { get; private set; }
    public TimeSpan CurrentPosition => _audioFile?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan CurrentDuration => _audioFile?.TotalTime ?? TimeSpan.Zero;
    public PlaybackState CurrentPlaybackState => _player?.PlaybackState ?? PlaybackState.Stopped;

    public event Action? OnSongChanged;
    public event Action? OnPlaybackStatusChanged;
    public event Action? OnPlaylistChanged;

    public void LoadSong(string filePath)
    {
        if (_player!.PlaybackState == PlaybackState.Playing || _player.PlaybackState == PlaybackState.Paused)
        {
            _player.Stop();
        }

        _audioFile?.Dispose();
        _audioFile = new AudioFileReader(filePath);
        _player!.Init(_audioFile);

        _playerInitialized = true;

        OnSongChanged?.Invoke();
    }

    public void Play()
    {
        if (!_playerInitialized)
            throw new InvalidOperationException("Please load a song first!");

        if (_player == null) return;

        if (_player.PlaybackState != PlaybackState.Playing)
        {
            _player.Play();
            OnPlaybackStatusChanged?.Invoke();
        }
    }

    public void Pause()
    {
        if (_player == null) return;

        if (_player.PlaybackState == PlaybackState.Playing)
        {
            _player.Pause();
            OnPlaybackStatusChanged?.Invoke();
        }
    }

    public void Stop()
    {
        if (_player == null) return;

        _player.Stop();
        OnPlaybackStatusChanged?.Invoke();
    }

    public void Seek(TimeSpan position)
    {
        if (_audioFile == null) return;
        _audioFile.CurrentTime = position;
    }

    public void SetQueue(List<Song> songs, int startIndex = 0)
    {
        _songQueue = songs;
        _songIndex = startIndex;
    }

    public void SetPlaylist(Playlist? playlist)
    {
        CurrentPlaylist = playlist;
        OnPlaylistChanged?.Invoke();
    }

    public List<Song> GetQueue()
    {
        return _songQueue;
    }

    public bool MoveToNextInQueue()
    {
        if (_songQueue.Count == 0) return false;

        if (_songIndex + 1 >= _songQueue.Count)
            _songIndex = 0;
        else
            _songIndex++;

        return true;
    }

    public bool MoveToPreviousInQueue()
    {
        if (_songQueue.Count == 0) return false;

        if (_songIndex - 1 < 0)
            _songIndex = _songQueue.Count - 1;
        else
            _songIndex--;

        return true;
    }

    public void Dispose()
    {
        _player?.Dispose();
        _audioFile?.Dispose();
    }
}
