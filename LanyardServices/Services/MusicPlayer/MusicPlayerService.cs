using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
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
        public bool IsShuffleEnabled { get; set; }
        public bool IsRepeatEnabled { get; set; } = true;
        public double LastKnownPositionSeconds { get; set; }
        public DateTime LastPositionUpdateUtc { get; set; } = DateTime.UtcNow;
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

    public bool GetShuffleEnabled(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            return state.IsShuffleEnabled;
        }
    }

    public bool GetRepeatEnabled(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            return state.IsRepeatEnabled;
        }
    }

    public double GetEstimatedPositionSeconds(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        DateTime nowUtc = DateTime.UtcNow;

        lock (_lock)
        {
            return EstimatePositionSeconds(state, nowUtc);
        }
    }

    public void UpdatePlaybackState(Guid clientId, PlaybackState state)
    {
        bool changed;

        ClientMusicState clientState = GetOrCreateState(clientId);
        DateTime nowUtc = DateTime.UtcNow;

        lock (_lock)
        {
            changed = clientState.CurrentState != state;

            if (clientState.CurrentState == PlaybackState.Playing && state != PlaybackState.Playing)
            {
                clientState.LastKnownPositionSeconds = EstimatePositionSeconds(clientState, nowUtc);
            }

            clientState.CurrentState = state;
            clientState.LastPositionUpdateUtc = nowUtc;

            if (state == PlaybackState.Stopped)
            {
                clientState.LastKnownPositionSeconds = 0;
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

            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = DateTime.UtcNow;
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
                state.LastKnownPositionSeconds = 0;
                state.LastPositionUpdateUtc = DateTime.UtcNow;
            }
        }

        if (changed)
        {
            OnSongChanged?.Invoke(clientId, song?.Id);
        }
    }

    private async Task SetQueue(Guid clientId, List<Song> songs, Guid? playlistId, int startIndex = 0)
    {
        ClientMusicState state = GetOrCreateState(clientId);

        ApplicationDbContext _context = await _contextFactory.CreateDbContextAsync();

        Playlist? playlist = await _context.Playlists.Where(x=> x.Id == playlistId).FirstOrDefaultAsync();

        lock (_lock)
        {
            state.Queue = songs;
            state.QueueIndex = startIndex;
            state.CurrentSong = songs.Count > 0 && startIndex < songs.Count ? songs[startIndex] : null;
            state.CurrentPlaylist = playlist;
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = DateTime.UtcNow;
        }

        OnSongChanged?.Invoke(clientId, state.CurrentSong?.Id);
    }

    private bool MoveToNext(Guid clientId)
    {
        Song? nextSong;
        ClientMusicState state = GetOrCreateState(clientId);
        DateTime nowUtc = DateTime.UtcNow;

        lock (_lock)
        {
            if (state.Queue.Count == 0)
            {
                return false;
            }

            int? nextIndex = GetNextQueueIndex(state);

            if (!nextIndex.HasValue)
            {
                return false;
            }

            state.QueueIndex = nextIndex.Value;
            nextSong = state.Queue[state.QueueIndex];
            state.CurrentSong = nextSong;
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = nowUtc;
        }

        OnSongChanged?.Invoke(clientId, nextSong?.Id);
        return true;
    }

    private bool MoveToPrevious(Guid clientId)
    {
        Song? previousSong;
        ClientMusicState state = GetOrCreateState(clientId);
        DateTime nowUtc = DateTime.UtcNow;

        lock (_lock)
        {
            if (state.Queue.Count == 0)
            {
                return false;
            }

            int? previousIndex = GetPreviousQueueIndex(state);

            if (!previousIndex.HasValue)
            {
                return false;
            }

            state.QueueIndex = previousIndex.Value;
            previousSong = state.Queue[state.QueueIndex];
            state.CurrentSong = previousSong;
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = nowUtc;
        }

        OnSongChanged?.Invoke(clientId, previousSong?.Id);
        return true;
    }

    private static int? GetNextQueueIndex(ClientMusicState state)
    {
        if (state.Queue.Count == 0)
        {
            return null;
        }

        if (state.IsShuffleEnabled)
        {
            if (state.Queue.Count == 1)
            {
                return state.IsRepeatEnabled ? 0 : null;
            }

            int nextIndex;
            do
            {
                nextIndex = _rng.Next(0, state.Queue.Count);
            } while (nextIndex == state.QueueIndex);

            return nextIndex;
        }

        int sequentialNext = state.QueueIndex + 1;
        if (sequentialNext >= state.Queue.Count)
        {
            return state.IsRepeatEnabled ? 0 : null;
        }

        return sequentialNext;
    }

    private static int? GetPreviousQueueIndex(ClientMusicState state)
    {
        if (state.Queue.Count == 0)
        {
            return null;
        }

        if (state.IsShuffleEnabled)
        {
            if (state.Queue.Count == 1)
            {
                return state.IsRepeatEnabled ? 0 : null;
            }

            int previousIndex;
            do
            {
                previousIndex = _rng.Next(0, state.Queue.Count);
            } while (previousIndex == state.QueueIndex);

            return previousIndex;
        }

        int sequentialPrevious = state.QueueIndex - 1;
        if (sequentialPrevious < 0)
        {
            return state.IsRepeatEnabled ? state.Queue.Count - 1 : null;
        }

        return sequentialPrevious;
    }

    private static double EstimatePositionSeconds(ClientMusicState state, DateTime nowUtc)
    {
        double positionSeconds = state.LastKnownPositionSeconds;

        if (state.CurrentState == PlaybackState.Playing)
        {
            positionSeconds += (nowUtc - state.LastPositionUpdateUtc).TotalSeconds;
        }

        if (state.CurrentSong is not null && state.CurrentSong.DurationSeconds > 0)
        {
            return Math.Clamp(positionSeconds, 0, state.CurrentSong.DurationSeconds);
        }

        return Math.Max(0, positionSeconds);
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

        await _hubContext.Clients.Client(connectionId).SendCoreAsync(methodName, args);
        return true;
    }

    public async Task LoadSongsIntoQueue(Guid clientId, Guid playlistId, Song? song = null)
    {
        List<Song> queueToSet;

        await using var context = await _contextFactory.CreateDbContextAsync();

        List<Song> playlistSongs = await context.PlaylistSongMembers
            .Where(x => x.PlaylistId == playlistId)
            .Select(x => x.Song!)
            .ToListAsync();

        playlistSongs = [.. playlistSongs.OrderBy(_ => _rng.Next())];

        if (song == null)
        {
            queueToSet = [.. playlistSongs];
        }
        else
        {
            queueToSet = [song, .. playlistSongs.Where(x => x.Id != song.Id)];
        }

        await SetQueue(clientId, queueToSet, playlistId, 0);

        await SendToClientAsync(clientId, "LoadPlaylist", queueToSet.Select(x => x.Id));
    }

    public async Task Play(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            state.LastPositionUpdateUtc = DateTime.UtcNow;
        }

        await SendToClientAsync(clientId, "Play");
    }

    public async Task Play(Guid clientId, Guid playlistId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            state.LastPositionUpdateUtc = DateTime.UtcNow;
        }

        await LoadSongsIntoQueue(clientId, playlistId);

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

        await LoadSongsIntoQueue(clientId, playlist?.Id ?? Guid.Empty, song);

        await SendToClientAsync(clientId, "Load", song.Id);

        ClientMusicState state = GetOrCreateState(clientId);

        lock (_lock)
        {
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = DateTime.UtcNow;
        }
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
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = DateTime.UtcNow;
        }

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
            Song? nextSong = GetCurrentSong(clientId);
            if (nextSong is null)
            {
                return;
            }

            await SendToClientAsync(clientId, "Load", nextSong.Id);
            await SendToClientAsync(clientId, "Play");
        }
    }

    public async Task Previous(Guid clientId)
    {
        if (MoveToPrevious(clientId))
        {
            Song? previousSong = GetCurrentSong(clientId);
            if (previousSong is null)
            {
                return;
            }

            await SendToClientAsync(clientId, "Load", previousSong.Id);
            await SendToClientAsync(clientId, "Play");
        }
    }

    public async Task Restart(Guid clientId)
    {
        Song? currentSong = GetCurrentSong(clientId);
        if (currentSong != null)
        {
            ClientMusicState state = GetOrCreateState(clientId);
            lock (_lock)
            {
                state.LastKnownPositionSeconds = 0;
                state.LastPositionUpdateUtc = DateTime.UtcNow;
            }

            await SendToClientAsync(clientId, "Load", currentSong.Id);
            await SendToClientAsync(clientId, "Play");
        }
    }

    public Task<bool> ToggleShuffle(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        bool isEnabled;

        lock (_lock)
        {
            state.IsShuffleEnabled = !state.IsShuffleEnabled;
            isEnabled = state.IsShuffleEnabled;
        }

        return Task.FromResult(isEnabled);
    }

    public Task<bool> ToggleRepeat(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        bool isEnabled;

        lock (_lock)
        {
            state.IsRepeatEnabled = !state.IsRepeatEnabled;
            isEnabled = state.IsRepeatEnabled;
        }

        return Task.FromResult(isEnabled);
    }

    public async Task Seek(Guid clientId, double positionSeconds)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        double normalizedSeconds;

        lock (_lock)
        {
            double duration = state.CurrentSong?.DurationSeconds ?? 0;
            normalizedSeconds = duration > 0 ? Math.Clamp(positionSeconds, 0, duration) : Math.Max(0, positionSeconds);
            state.LastKnownPositionSeconds = normalizedSeconds;
            state.LastPositionUpdateUtc = DateTime.UtcNow;
        }

        await SendToClientAsync(clientId, "Seek", normalizedSeconds);
    }

    public async Task GetCachedSongsAsync(Guid clientId)
    {
        await SendToClientAsync(clientId, "GetCachedSongs");
    }
}
