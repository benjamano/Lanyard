using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Lanyard.Application.Services;

/// <summary>
/// Service for controlling music playback via SignalR.
/// Sends commands to remote music players and maintains playback state.
/// </summary>
public class MusicPlayerService
{
    private readonly IHubContext<SignalRControlHub> _hubContext;
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
    private readonly ILogger<MusicPlayerService> _logger;
    private readonly object _lock = new();
    private static readonly Random _rng = new();

    private readonly Dictionary<Guid, ClientMusicState> _stateByClientId = [];

    public MusicPlayerService(
        IHubContext<SignalRControlHub> hubContext,
        IDbContextFactory<ApplicationDbContext> contextFactory,
        ILogger<MusicPlayerService> logger)
    {
        _hubContext = hubContext;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public event Action<Guid, PlaybackState>? OnPlaybackStatusChanged;
    public event Action<Guid, Guid?>? OnSongChanged;

    private sealed class ClientMusicState
    {
        public PlaybackState CurrentState { get; set; } = PlaybackState.Stopped;
        public Song? CurrentSong { get; set; }
        public Playlist? CurrentPlaylist { get; set; }
        public List<Song> Queue { get; set; } = [];
        public int QueueIndex { get; set; }
    }

    private ClientMusicState GetOrCreateState(Guid clientId)
    {
        lock (_lock)
        {
            if (!_stateByClientId.TryGetValue(clientId, out ClientMusicState? state))
            {
                state = new ClientMusicState();
                _stateByClientId[clientId] = state;
            }

            return state;
        }
    }

    public PlaybackState GetCurrentPlaybackState(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            return state.CurrentState;
        }
    }

    public Song? GetCurrentSong(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            return state.CurrentSong;
        }
    }

    public Playlist? GetCurrentPlaylist(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            return state.CurrentPlaylist;
        }
    }

    public void UpdatePlaybackState(Guid clientId, PlaybackState state)
    {
        bool changed;

        ClientMusicState clientState = GetOrCreateState(clientId);

        lock (_lock)
        {
            changed = clientState.CurrentState != state;
            if (changed)
            {
                clientState.CurrentState = state;
            }
        }

        if (changed)
        {
            OnPlaybackStatusChanged?.Invoke(clientId, state);
        }
    }

    public void UpdateCurrentSong(Guid clientId, Guid songId)
    {
        Song? songFromQueue;

        ClientMusicState state = GetOrCreateState(clientId);

        lock (_lock)
        {
            songFromQueue = state.Queue.FirstOrDefault(x => x.Id == songId);

            if (songFromQueue != null)
            {
                state.CurrentSong = songFromQueue;
                int queueIndex = state.Queue.FindIndex(x => x.Id == songId);
                if (queueIndex >= 0)
                {
                    state.QueueIndex = queueIndex;
                }
            }
        }

        OnSongChanged?.Invoke(clientId, songId);
    }

    private void SetCurrentSong(Guid clientId, Song? song)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        bool changed;

        lock (_lock)
        {
            changed = state.CurrentSong?.Id != song?.Id;
            if (changed)
            {
                state.CurrentSong = song;
            }
        }

        if (changed)
        {
            OnSongChanged?.Invoke(clientId, song?.Id);
        }
    }

    private void SetQueue(Guid clientId, List<Song> songs, Playlist? playlist, int startIndex = 0)
    {
        ClientMusicState state = GetOrCreateState(clientId);

        lock (_lock)
        {
            state.Queue = songs;
            state.QueueIndex = startIndex;
            state.CurrentSong = songs.Count > 0 && startIndex < songs.Count ? songs[startIndex] : null;
            state.CurrentPlaylist = playlist;
        }

        OnSongChanged?.Invoke(clientId, state.CurrentSong?.Id);
    }

    private bool MoveToNext(Guid clientId)
    {
        Song? nextSong;
        ClientMusicState state = GetOrCreateState(clientId);

        lock (_lock)
        {
            if (state.Queue.Count == 0)
            {
                return false;
            }

            state.QueueIndex = (state.QueueIndex + 1) % state.Queue.Count;
            nextSong = state.Queue[state.QueueIndex];
            state.CurrentSong = nextSong;
        }

        OnSongChanged?.Invoke(clientId, nextSong?.Id);
        return true;
    }

    private bool MoveToPrevious(Guid clientId)
    {
        Song? previousSong;
        ClientMusicState state = GetOrCreateState(clientId);

        lock (_lock)
        {
            if (state.Queue.Count == 0)
            {
                return false;
            }

            state.QueueIndex = state.QueueIndex - 1 < 0 ? state.Queue.Count - 1 : state.QueueIndex - 1;
            previousSong = state.Queue[state.QueueIndex];
            state.CurrentSong = previousSong;
        }

        OnSongChanged?.Invoke(clientId, previousSong?.Id);
        return true;
    }

    private async Task<string?> GetClientConnectionIdAsync(Guid clientId)
    {
        await using ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();

        string? connectionId = await context.Clients
            .AsNoTracking()
            .Where(x => x.Id == clientId)
            .Select(x => x.MostRecentConnectionId)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(connectionId))
        {
            _logger.LogWarning("Could not resolve connection for client {ClientId}", clientId);
            return null;
        }

        return connectionId;
    }

    private async Task<bool> SendToClientAsync(Guid clientId, string methodName, params object?[] args)
    {
        string? connectionId = await GetClientConnectionIdAsync(clientId);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return false;
        }

        await _hubContext.Clients.Client(connectionId).SendAsync(methodName, args);
        return true;
    }

    public async Task Play(Guid clientId)
    {
        await SendToClientAsync(clientId, "Play");
    }

    public async Task Play(Guid clientId, Song song, Playlist? playlist = null)
    {
        Song? currentSong = GetCurrentSong(clientId);

        if (song.Id == currentSong?.Id)
        {
            await Play(clientId);
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

            await SendToClientAsync(clientId, "LoadPlaylist", queueToSet.Select(x => x.Id));
        }
        else
        {
            queueToSet = [song];
        }

        SetQueue(clientId, queueToSet, playlist, 0);

        await SendToClientAsync(clientId, "Load", song.Id);
        await SendToClientAsync(clientId, "Play");
    }

    public async Task Pause(Guid clientId)
    {
        await SendToClientAsync(clientId, "Pause");
    }

    public async Task TogglePlay(Guid clientId)
    {
        if (GetCurrentPlaybackState(clientId) == PlaybackState.Playing)
        {
            await Pause(clientId);
        }
        else
        {
            await Play(clientId);
        }
    }

    public async Task Stop(Guid clientId)
    {
        await SendToClientAsync(clientId, "Stop");
    }

    public async Task Next(Guid clientId)
    {
        Song? currentSong = GetCurrentSong(clientId);
        if (currentSong == null)
        {
            return;
        }

        if (MoveToNext(clientId))
        {
            await SendToClientAsync(clientId, "PlayNext");
        }
    }

    public async Task Previous(Guid clientId)
    {
        if (MoveToPrevious(clientId))
        {
            await SendToClientAsync(clientId, "PlayPrevious");
        }
    }

    public async Task Restart(Guid clientId)
    {
        Song? currentSong = GetCurrentSong(clientId);
        if (currentSong != null)
        {
            await SendToClientAsync(clientId, "Load", currentSong.Id);
            await SendToClientAsync(clientId, "Play");
        }
    }
}
