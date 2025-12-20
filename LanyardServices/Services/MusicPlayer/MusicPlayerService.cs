using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using NAudio.Wave;

namespace Lanyard.Application.Services;

public class MusicPlayerService(MusicPlayer player, IDbContextFactory<ApplicationDbContext> contextFactory)
{
    private readonly MusicPlayer _player = player;
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory = contextFactory;
    private static readonly Random _rng = new();

    public Song? CurrentSong => _player.CurrentSong;
    public Playlist? CurrentPlaylist => _player.CurrentPlaylist;
    public TimeSpan CurrentPosition => _player.CurrentPosition;
    public TimeSpan CurrentDuration => _player.CurrentDuration;
    public PlaybackState CurrentPlaybackState => _player.CurrentPlaybackState;

    public event Action? OnSongChanged
    {
        add => _player.OnSongChanged += value;
        remove => _player.OnSongChanged -= value;
    }

    public event Action? OnPlaybackStatusChanged
    {
        add => _player.OnPlaybackStatusChanged += value;
        remove => _player.OnPlaybackStatusChanged -= value;
    }

    public event Action? OnPlaylistChanged
    {
        add => _player.OnPlaylistChanged += value;
        remove => _player.OnPlaylistChanged -= value;
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
        if (_player.CurrentSong != null)
        {
            await VerifySongMetaData(_player.CurrentSong);
        }

        _player.Play();
    }

    public async Task Play(Song song)
    {
        if (song == _player.CurrentSong)
        {
            await Play();
            return;
        }

        _player.SetQueue([song], 0);
        _player.LoadSong(song.FilePath);

        _player.SetPlaylist(null);

        await Play();
    }

    public async Task Play(Song song, Playlist playlist)
    {
        if (song == _player.CurrentSong)
        {
            await Play();
            return;
        }

        if (playlist == null)
        {
            await Play(song);
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync();
        
        List<Song> playlistSongs = [.. (await context.PlaylistSongMembers
            .Where(x => x.PlaylistId == playlist.Id)
            .Select(x => x.Song!)
            .ToListAsync())
        .OrderBy(_ => _rng.Next())];

        List<Song> songsInPlaylist = [song];
        songsInPlaylist.AddRange(playlistSongs.Where(x => x.Id != song.Id).ToList());

        _player.SetQueue(songsInPlaylist, 0);
        _player.LoadSong(song.FilePath);

        Playlist loadedPlaylist = await context.Playlists
            .Where(x => x.Id == playlist.Id)
            .FirstOrDefaultAsync() 
            ?? throw new InvalidOperationException("Playlist not found!");
        
        _player.SetPlaylist(loadedPlaylist);

        await Play();
    }

    public void Pause()
    {
        _player.Pause();
    }

    public async Task TogglePlay()
    {
        if (_player.CurrentPlaybackState == PlaybackState.Playing)
        {
            _player.Pause();
        }
        else
        {
            await Play();
        }
    }

    public void Stop()
    {
        _player.Stop();
    }

    public async Task Next()
    {
        if (_player.CurrentSong == null) return;

        _player.Pause();

        if (_player.MoveToNextInQueue())
        {
            _player.LoadSong(_player.CurrentSong.FilePath);
            await Play();
        }
    }

    public async Task Previous()
    {
        _player.Pause();

        if (_player.MoveToPreviousInQueue())
        {
            _player.LoadSong(_player.CurrentSong!.FilePath);
            await Play();
        }
    }

    public async Task Restart()
    {
        _player.Pause();
        _player.Seek(TimeSpan.Zero);
        await Play();
    }

    public List<Song> GetQueue()
    {
        return _player.GetQueue();
    }

    public async Task LoadPlaylist(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        
        List<Song> songs = [.. (await context.PlaylistSongMembers
            .Where(x => x.PlaylistId == playlistId)
            .Select(x => x.Song!)
            .ToListAsync())
        .OrderBy(_ => _rng.Next())];
        
        _player.SetQueue(songs, 0);
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