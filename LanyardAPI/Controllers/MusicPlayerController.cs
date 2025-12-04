using NAudio.Wave;
using LanyardData.Models;
using LanyardData.DataAccess;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public class MusicPlayerService(ApplicationDbContext context) : IDisposable
{
    private readonly ApplicationDbContext _context = context;
    private static readonly Random rng = new();

    private WaveOutEvent? player = new();
    private AudioFileReader? audioFile;

    private bool _playerInitialized = false;
    private List<Song> _songQueue = [];
    private int _songIndex = 0;

    public void LoadSong(string filePath)
    {
        audioFile?.Dispose();
        audioFile = new AudioFileReader(filePath);
        player!.Init(audioFile);

        _playerInitialized = true;
    }

    public void Play()
    {
        if (!_playerInitialized)
            throw new InvalidOperationException("Please load a song first!");

        if (player == null) return;

        if (player.PlaybackState != PlaybackState.Playing)
            player.Play();
    }

    public void Pause()
    {
        if (player == null) return;

        if (player.PlaybackState == PlaybackState.Playing)
            player.Pause();
    }

    public void Stop()
    {
        if (player == null) return;

        player.Stop();
    }

    public void Next()
    {
        if (player == null) return;

        Pause();

        if (_songIndex + 1 >= _songQueue.Count)
            _songIndex = 0;
        else
            _songIndex++;

        LoadSong(_songQueue[_songIndex].FilePath);

        Play();
    }

    public void Previous()
    {
        if (player == null) return;

        Pause();

        if (_songIndex - 1 < 0)
            _songIndex = _songQueue.Count - 1;
        else
            _songIndex--;

        LoadSong(_songQueue[_songIndex].FilePath);

        Play();
    }

    public List<Song> GetQueue()
    {
        return _songQueue;
    }

    public async Task LoadPlaylist(Guid PlayListId)
    {
        Playlist? playlist = await _context.Playlists
            .Where(x => x.Id == PlayListId)
            .FirstOrDefaultAsync();

        if (playlist == null)
            throw new InvalidOperationException("Playlist not found!");

        _songQueue = await _context.PlaylistSongMembers
            .Where(x => x.PlaylistId == PlayListId)
            .OrderBy(_ => rng.Next())
            .Select(x=> x.Song!)
            .ToListAsync();

        _songIndex = 0;
    }

    public void Dispose()
    {
        player?.Dispose();
        audioFile?.Dispose();
    }
}