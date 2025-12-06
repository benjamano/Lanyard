using LanyardData.DataAccess;
using LanyardData.Models;
using LanyardAPI.Services;
using Microsoft.EntityFrameworkCore;
using NAudio.Wave;
using System.Diagnostics;
using System.Threading.Tasks;
using TagLib;

public class MusicPlayerService(IServiceProvider serviceProvider) : IDisposable
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private WaveOutEvent? player = new();
    private AudioFileReader? audioFile;

    private bool _playerInitialized = false;
    private List<Song> _songQueue = [];
    private int _songIndex = 0;

    public Song? CurrentSong => _songQueue.Count > 0 && _songIndex < _songQueue.Count ? _songQueue[_songIndex] : null;
    public TimeSpan CurrentPosition => audioFile?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan CurrentDuration => audioFile?.TotalTime ?? TimeSpan.Zero;
    public PlaybackState CurrentPlaybackState => player?.PlaybackState ?? PlaybackState.Stopped;

    public event Action? OnSongChanged;
    public event Action? OnPlaybackStatusChanged;

    public async Task VerifySongMetaData(Song song)
    {
        if (song.DurationSeconds == 0)
        {
            TagLib.File tfile = TagLib.File.Create(song.FilePath);

            int durationSeconds = (int)tfile.Properties.Duration.TotalSeconds;

            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<MusicRepository>();
            
            await repository.UpdateSongDuration(song, durationSeconds);
        }
    }

    public void LoadSong(string filePath)
    {
        if (player!.PlaybackState == PlaybackState.Playing || player.PlaybackState == PlaybackState.Paused)
        {
            player.Stop();
        }

        audioFile?.Dispose();
        audioFile = new AudioFileReader(filePath);
        player!.Init(audioFile);

        _playerInitialized = true;
        
        OnSongChanged?.Invoke();
    }

    public async Task Play()
    {
        if (!_playerInitialized)
            throw new InvalidOperationException("Please load a song first!");

        if (player == null) return;

        await VerifySongMetaData(_songQueue[_songIndex]);

        if (player.PlaybackState != PlaybackState.Playing)
        {
            player.Play();

            OnPlaybackStatusChanged?.Invoke();
        }
    }

    public async Task TogglePlay()
    {
        if (player == null) return;

        if (player.PlaybackState == PlaybackState.Playing)
        {
            Pause();
        }
        else
        {
            await Play();
        }

    }

    public async Task Play(Song song)
    {
        if (song == CurrentSong)
        {
            await Play();
            return;
        }

        if (_songQueue.Count == 0)
        {
            _songQueue.Add(song);
        }
        else
        {
            _songQueue[0] = song;
        }

        _songIndex = 0;

        LoadSong(song.FilePath);

        await Play();
    }

    public void Pause()
    {
        if (player == null) return;

        if (player.PlaybackState == PlaybackState.Playing)
        {
            player.Pause();

            OnPlaybackStatusChanged?.Invoke();
        }
    }

    public async Task Stop()
    {
        if (player == null) return;

        player.Stop();

        OnPlaybackStatusChanged?.Invoke();
    }

    public async Task Next()
    {
        if (player == null || CurrentSong == null) return;

        Pause();

        if (_songIndex + 1 >= _songQueue.Count)
            _songIndex = 0;
        else
            _songIndex++;

        LoadSong(_songQueue[_songIndex].FilePath);

        await Play();
    }

    public async Task Previous()
    {
        if (player == null) return;

        Pause();

        if (_songIndex - 1 < 0)
            _songIndex = _songQueue.Count - 1;
        else
            _songIndex--;

        LoadSong(_songQueue[_songIndex].FilePath);

        await Play();
    }

    public async Task Restart()
    {
        if (player == null || audioFile == null) return;

        Pause();

        audioFile!.CurrentTime = TimeSpan.Zero;

        await Play();
    }

    public List<Song> GetQueue()
    {
        return _songQueue;
    }

    public async Task LoadPlaylist(Guid PlayListId)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<MusicRepository>();
        
        await repository.GetPlaylistById(PlayListId);

        _songQueue = await repository.GetPlaylistSongsRandomized(PlayListId);

        _songIndex = 0;
    }

    public async Task<IEnumerable<Song>> GetLocalSongs()
    {
        List<Song> songs = new();

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<MusicRepository>();
        
        List<string> existingPaths = await repository.GetExistingSongFilePaths();

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

    public void Dispose()
    {
        player?.Dispose();
        audioFile?.Dispose();
    }
}