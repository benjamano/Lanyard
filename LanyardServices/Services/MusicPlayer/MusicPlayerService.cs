using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.SignalR;
using Lanyard.Infrastructure.DTO;
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
    public event Action<Playlist?>? OnPlaylistChanged;
    public event Action<List<Song>>? QueueChanged;

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

    public int QueueIndex
    {
        get
        {
            lock (_lock)
            {
                return _queueIndex;
            }
        }
    }

    public void UpdatePlaybackState(PlaybackState state)
    {
        bool changed = false;

        lock (_lock)
        {
            if (_currentState != state)
            {
                _currentState = state;
                changed = true;
            }
        }

        if (changed)
        {
            OnPlaybackStatusChanged?.Invoke(state);
        }
    }

    private void UpdateCurrentSong(Song? song)
    {
        bool changed = false;

        lock (_lock)
        {
            if (_currentSong?.Id != song?.Id)
            {
                _currentSong = song;
                changed = true;
            }
        }

        if (changed)
        {
            OnSongChanged?.Invoke(song?.Id);
        }
    }

    private void UpdateCurrentPlaylist(Playlist? playlist)
    {
        bool changed = false;

        lock (_lock)
        {
            if (_currentPlaylist?.Id != playlist?.Id)
            {
                _currentPlaylist = playlist;
                changed = true;
            }
        }

        if (changed)
        {
            OnPlaylistChanged?.Invoke(playlist);
        }
    }

    private void SetQueue(List<Song> songs, int startIndex = 0)
    {
        lock (_lock)
        {
            _queue = songs;
            _queueIndex = startIndex;

            if (songs.Count > 0 && startIndex < songs.Count)
            {
                _currentSong = songs[startIndex];
            }
        }

        QueueChanged?.Invoke(songs);
        OnSongChanged?.Invoke(_currentSong?.Id);
    }

    private bool MoveToNextInQueue()
    {
        lock (_lock)
        {
            if (_queue.Count == 0) return false;

            if (_queueIndex + 1 >= _queue.Count)
                _queueIndex = 0;
            else
                _queueIndex++;

            _currentSong = _queue[_queueIndex];
        }

        OnSongChanged?.Invoke(_currentSong?.Id);
        return true;
    }

    private bool MoveToPreviousInQueue()
    {
        lock (_lock)
        {
            if (_queue.Count == 0) return false;

            if (_queueIndex - 1 < 0)
                _queueIndex = _queue.Count - 1;
            else
                _queueIndex--;

            _currentSong = _queue[_queueIndex];
        }

        OnSongChanged?.Invoke(_currentSong?.Id);
        return true;
    }

    private async Task VerifySongMetaData(Song song)
    {
        if (song.DurationSeconds == 0)
        {
            TagLib.File tfile = TagLib.File.Create(song.FilePath);
            int durationSeconds = (int)tfile.Properties.Duration.TotalSeconds;
            
            await using var context = await _contextFactory.CreateDbContextAsync();
            song.DurationSeconds = durationSeconds;
            context.Songs.Update(song);
            await context.SaveChangesAsync();
        }
    }

    public async Task Play()
    {
        if (_currentSong != null)
        {
            await VerifySongMetaData(_currentSong);
        }

        await _hubContext.Clients.Group("Music").SendAsync("Play");
    }

    public async Task Play(Song song)
    {
        if (song == _currentSong)
        {
            await Play();
            return;
        }

        SetQueue([song], 0);

        await _hubContext.Clients.Group("Music").SendAsync("Load", song.Id);
        await _hubContext.Clients.Group("Music").SendAsync("Play");
    }

    public async Task Play(Song song, Playlist playlist)
    {
        if (song == _currentSong)
        {
            await Play();
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync();

        if (playlist is not null)
        {
            List<Song> playlistSongs = [.. (await context.PlaylistSongMembers
            .Where(x => x.PlaylistId == playlist.Id)
            .Select(x => x.Song!)
            .ToListAsync())
            .OrderBy(_ => _rng.Next())];

            List<Song> songsInPlaylist = [song];
            songsInPlaylist.AddRange(playlistSongs.Where(x => x.Id != song.Id).ToList());

            SetQueue(songsInPlaylist, 0);

            Playlist loadedPlaylist = await context.Playlists
                .Where(x => x.Id == playlist.Id)
                .FirstOrDefaultAsync()
                ?? throw new InvalidOperationException("Playlist not found!");

            UpdateCurrentPlaylist(loadedPlaylist);

            await _hubContext.Clients.Group("Music").SendAsync("LoadPlaylist", playlistSongs.Select(x=> x.Id));
        }

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

        if (MoveToNextInQueue())
        {
            await _hubContext.Clients.Group("Music").SendAsync("PlayNext");
        }
    }

    public async Task Previous()
    {
        if (MoveToPreviousInQueue())
        {
            Song? previousSong = _currentSong;
            if (previousSong != null)
            {
                await _hubContext.Clients.Group("Music").SendAsync("PlayPrevious");
            }
        }
    }

    public async Task Restart()
    {
        await Pause();
        
        Song? currentSong = _currentSong;
        if (currentSong != null)
        {
            await _hubContext.Clients.Group("Music").SendAsync("Load", currentSong.Id);
            await _hubContext.Clients.Group("Music").SendAsync("Play");
        }
    }

    public List<Song> GetQueue()
    {
        return Queue;
    }

    public async Task LoadPlaylist(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        List<Song> songs = [.. (await context.PlaylistSongMembers
            .Where(x => x.PlaylistId == playlistId)
            .Select(x => x.Song!)
            .ToListAsync())
        .OrderBy(_ => _rng.Next())];
        
        SetQueue(songs, 0);
    }

    public async Task<IEnumerable<Song>> GetLocalSongs()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        List<Song> songs = [];
        List<string> existingPaths = await context.Songs
            .Select(x => x.FilePath)
            .ToListAsync();
        
        var existingFileNames = existingPaths.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        if (Directory.Exists(musicFolder))
        {
            IEnumerable<string> files = Directory.EnumerateFiles(musicFolder, "*.mp3", SearchOption.AllDirectories)
                .Where(f => !existingFileNames.Contains(Path.GetFileName(f)));

            foreach (string file in files)
            {
                TagLib.File tfile = TagLib.File.Create(file);

                songs.Add(new Song
                {
                    Id = Guid.NewGuid(),
                    Name = Path.GetFileNameWithoutExtension(file),
                    CreateDate = System.IO.File.GetCreationTimeUtc(file),
                    AlbumName = "Local Music",
                    FilePath = file,
                    DurationSeconds = (int)tfile.Properties.Duration.TotalSeconds
                });
            }
        }

        return songs;
    }
}