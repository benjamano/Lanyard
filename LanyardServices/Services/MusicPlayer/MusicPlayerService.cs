using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NAudio.Wave;

namespace Lanyard.Application.Services;

/// <summary>
/// Service for controlling music playback via SignalR.
/// Sends commands to remote music players and maintains playback state.
/// </summary>
public class MusicPlayerService
{
    private readonly IHubContext<MusicControlHub> _hubContext;
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
    private readonly object _lock = new();
    private static readonly Random _rng = new();

    private PlaybackState _currentState = PlaybackState.Stopped;
    private Song? _currentSong;
    private Playlist? _currentPlaylist;
    private List<Song> _queue = [];
    private int _queueIndex = 0;

    public MusicPlayerService(
        IHubContext<MusicControlHub> hubContext,
        IDbContextFactory<ApplicationDbContext> contextFactory)
    {
        _hubContext = hubContext;
        _contextFactory = contextFactory;
    }

    public event Action<PlaybackState>? OnPlaybackStatusChanged;
    public event Action<Guid?>? OnSongChanged;

    public PlaybackState CurrentPlaybackState
    {
        get
        {
            lock (_lock)
            {
                return _currentState;
            }
        }
    }

    public Song? CurrentSong
    {
        get
        {
            lock (_lock)
            {
                return _currentSong;
            }
        }
    }

    public Playlist? CurrentPlaylist
    {
        get
        {
            lock (_lock)
            {
                return _currentPlaylist;
            }
        }
    }

    public List<Song> Queue
    {
        get
        {
            lock (_lock)
            {
                return [.. _queue];
            }
        }
    }

    public void UpdatePlaybackState(PlaybackState state)
    {
        bool changed;

        lock (_lock)
        {
            changed = _currentState != state;
            if (changed)
            {
                _currentState = state;
            }
        }

        if (changed)
        {
            OnPlaybackStatusChanged?.Invoke(state);
        }
    }

    private void SetCurrentSong(Song? song)
    {
        bool changed;

        lock (_lock)
        {
            changed = _currentSong?.Id != song?.Id;
            if (changed)
            {
                _currentSong = song;
            }
        }

        if (changed)
        {
            OnSongChanged?.Invoke(song?.Id);
        }
    }

    private void SetQueue(List<Song> songs, int startIndex = 0)
    {
        lock (_lock)
        {
            _queue = songs;
            _queueIndex = startIndex;
            _currentSong = songs.Count > 0 && startIndex < songs.Count ? songs[startIndex] : null;
        }

        OnSongChanged?.Invoke(_currentSong?.Id);
    }

    private bool MoveToNext()
    {
        Song? nextSong;

        lock (_lock)
        {
            if (_queue.Count == 0) return false;

            _queueIndex = (_queueIndex + 1) % _queue.Count;
            nextSong = _queue[_queueIndex];
            _currentSong = nextSong;
        }

        OnSongChanged?.Invoke(nextSong?.Id);
        return true;
    }

    private bool MoveToPrevious()
    {
        Song? previousSong;

        lock (_lock)
        {
            if (_queue.Count == 0) return false;

            _queueIndex = _queueIndex - 1 < 0 ? _queue.Count - 1 : _queueIndex - 1;
            previousSong = _queue[_queueIndex];
            _currentSong = previousSong;
        }

        OnSongChanged?.Invoke(previousSong?.Id);
        return true;
    }

    public async Task Play()
    {
        await _hubContext.Clients.Group("Music").SendAsync("Play");
    }

    public async Task Play(Song song, Playlist? playlist = null)
    {
        if (song == _currentSong)
        {
            await Play();
            return;
        }

        List<Song> queueToSet;

        if (playlist is not null)
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            List<Song> playlistSongs = await context.PlaylistSongMembers
                .Where(x => x.PlaylistId == playlist.Id)
                .Select(x => x.Song!)
                .ToListAsync();

            playlistSongs = [.. playlistSongs.OrderBy(_ => _rng.Next())];

            queueToSet = [song, .. playlistSongs.Where(x => x.Id != song.Id)];

            lock (_lock)
            {
                _currentPlaylist = playlist;
            }

            await _hubContext.Clients.Group("Music").SendAsync("LoadPlaylist", playlistSongs.Select(x => x.Id));
        }
        else
        {
            queueToSet = [song];
            
            lock (_lock)
            {
                _currentPlaylist = null;
            }
        }

        SetQueue(queueToSet, 0);

        await _hubContext.Clients.Group("Music").SendAsync("Load", song.Id);
        await _hubContext.Clients.Group("Music").SendAsync("Play");
    }

    public async Task Pause()
    {
        await _hubContext.Clients.Group("Music").SendAsync("Pause");
    }

    public async Task TogglePlay()
    {
        if (_currentState == PlaybackState.Playing)
        {
            await Pause();
        }
        else
        {
            await Play();
        }
    }

    public async Task Stop()
    {
        await _hubContext.Clients.Group("Music").SendAsync("Stop");
    }

    public async Task Next()
    {
        if (_currentSong == null) return;

        if (MoveToNext())
        {
            await _hubContext.Clients.Group("Music").SendAsync("PlayNext");
        }
    }

    public async Task Previous()
    {
        if (MoveToPrevious())
        {
            await _hubContext.Clients.Group("Music").SendAsync("PlayPrevious");
        }
    }

    public async Task Restart()
    {
        Song? currentSong = _currentSong;
        if (currentSong != null)
        {
            await _hubContext.Clients.Group("Music").SendAsync("Load", currentSong.Id);
            await _hubContext.Clients.Group("Music").SendAsync("Play");
        }
    }
}