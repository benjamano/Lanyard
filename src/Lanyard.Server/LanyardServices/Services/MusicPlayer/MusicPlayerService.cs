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
    public event Action<Guid, bool>? OnShuffleStateChanged;

    private sealed class ClientMusicState
    {
        public PlaybackState CurrentState { get; set; } = PlaybackState.Stopped;
        public Song? CurrentSong { get; set; }
        public Playlist? CurrentPlaylist { get; set; }
        public List<Song> Queue { get; set; } = [];
        public int QueueIndex { get; set; }
        public bool IsShuffleEnabled { get; set; }
        // Defaults to looping the queue so unattended playback keeps going without someone
        // having to arm repeat on every client.
        public RepeatMode RepeatMode { get; set; } = RepeatMode.All;
        public double LastKnownPositionSeconds { get; set; }
        public DateTime LastPositionUpdateUtc { get; set; } = DateTime.UtcNow;
        public int CurrentVolume { get; set; }
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

    public async Task<Result<bool>> SetCurrentPlaylist(Guid clientId, Guid playlistId)
    {
        ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();

        Playlist? playlist = await context.Playlists
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.Id == playlistId)
            .FirstOrDefaultAsync();

        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            state.CurrentPlaylist = playlist;
        }

        return Result<bool>.Ok(true);
    }

    public bool GetShuffleEnabled(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            return state.IsShuffleEnabled;
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

        Playlist? playlist = await _context.Playlists
            .Where(x=> x.Id == playlistId)
            .FirstOrDefaultAsync();

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

    public async Task SetQueue(Guid clientId, List<Guid> songIds, Guid? playlistId)
    {
        List<Song> songs;

        await using ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();

        songs = await context.Songs
            .AsNoTracking()
            .Where(x => songIds.Contains(x.Id))
            .OrderByDescending(x=> x.CreateDate)
            .ToListAsync();

        await SetQueue(clientId, songs, playlistId);
    }

    private bool MoveToNext(Guid clientId, bool isAutomaticAdvance)
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

            int? nextIndex = GetNextQueueIndex(state, isAutomaticAdvance);

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

    /// <param name="isAutomaticAdvance">
    /// True when the current track ended by itself, false when the user pressed Next.
    /// Only an automatic advance honours <see cref="RepeatMode.One"/> — an explicit skip
    /// means the user wants to move on, so it advances regardless.
    /// </param>
    private static int? GetNextQueueIndex(ClientMusicState state, bool isAutomaticAdvance)
    {
        if (state.Queue.Count == 0)
        {
            return null;
        }

        if (isAutomaticAdvance && state.RepeatMode == RepeatMode.One)
        {
            return state.QueueIndex;
        }

        // Any repeat mode keeps the queue circular; only Off runs off the end and stops.
        bool wrapsAround = state.RepeatMode != RepeatMode.Off;

        if (state.IsShuffleEnabled)
        {
            if (state.Queue.Count == 1)
            {
                return wrapsAround ? 0 : null;
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
            return wrapsAround ? 0 : null;
        }

        return sequentialNext;
    }

    private static int? GetPreviousQueueIndex(ClientMusicState state)
    {
        if (state.Queue.Count == 0)
        {
            return null;
        }

        bool wrapsAround = state.RepeatMode != RepeatMode.Off;

        if (state.IsShuffleEnabled)
        {
            if (state.Queue.Count == 1)
            {
                return wrapsAround ? 0 : null;
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
            return wrapsAround ? state.Queue.Count - 1 : null;
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
            .OrderByDescending(x=> x.CreateDate)
            .ToListAsync();

        if (GetShuffleEnabled(clientId))
        {
            playlistSongs = [.. playlistSongs.OrderBy(_ => _rng.Next())];
        }

        if (song == null)
        {
            queueToSet = [.. playlistSongs];
        }
        else
        {
            queueToSet = [song, .. playlistSongs.Where(x => x.Id != song.Id)];
        }

        await SetQueue(clientId, queueToSet, playlistId, 0);

        await SendToClientAsync(clientId, "LoadPlaylist", queueToSet.ToDictionary(x => x.Id, _ => playlistId));
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

        await SendToClientAsync(clientId, "Load", song.Id, playlist?.Id ?? Guid.Empty);

        ClientMusicState state = GetOrCreateState(clientId);

        lock (_lock)
        {
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = DateTime.UtcNow;
        }
        await SendToClientAsync(clientId, "Play");
    }

    /// <summary>
    /// Called when a client reports that its track finished on its own. Advances the queue via
    /// the same repeat/shuffle rules the Next button uses; with repeat on that resolves back to
    /// the current track, which replays it. Stops quietly at the end of a non-repeating queue.
    /// </summary>
    public async Task HandleSongEndedAsync(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);

        lock (_lock)
        {
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = DateTime.UtcNow;
        }

        if (GetCurrentSong(clientId) is null)
        {
            return;
        }

        if (!MoveToNext(clientId, isAutomaticAdvance: true))
        {
            _logger.LogInformation("Client {ClientId} reached the end of its queue", clientId);
            return;
        }

        Song? nextSong = GetCurrentSong(clientId);
        if (nextSong is null)
        {
            return;
        }

        Guid currentPlaylistId;
        lock (_lock)
        {
            currentPlaylistId = state.CurrentPlaylist?.Id ?? Guid.Empty;
        }

        _logger.LogInformation("Client {ClientId} advancing to song {SongId} after track end", clientId, nextSong.Id);

        await SendToClientAsync(clientId, "Load", nextSong.Id, currentPlaylistId);
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

        ClientMusicState state = GetOrCreateState(clientId);

        if (MoveToNext(clientId, isAutomaticAdvance: false))
        {
            Song? nextSong = GetCurrentSong(clientId);
            Guid currentPlaylistId = state.CurrentPlaylist?.Id ?? Guid.Empty;
            if (nextSong is null)
            {
                return;
            }

            await SendToClientAsync(clientId, "Load", nextSong.Id, currentPlaylistId);
            await SendToClientAsync(clientId, "Play");
        }
    }

    public async Task Previous(Guid clientId)
    {
        if (MoveToPrevious(clientId))
        {
            Song? previousSong = GetCurrentSong(clientId);
            Guid currentPlaylistId = GetOrCreateState(clientId).CurrentPlaylist?.Id ?? Guid.Empty;
            if (previousSong is null)
            {
                return;
            }

            await SendToClientAsync(clientId, "Load", previousSong.Id, currentPlaylistId);
            await SendToClientAsync(clientId, "Play");
        }
    }

    public async Task Restart(Guid clientId)
    {
        Song? currentSong = GetCurrentSong(clientId);
        if (currentSong != null)
        {
            Guid currentPlaylistId = Guid.Empty;

            ClientMusicState state = GetOrCreateState(clientId);
            lock (_lock)
            {
                state.LastKnownPositionSeconds = 0;
                state.LastPositionUpdateUtc = DateTime.UtcNow;
                currentPlaylistId = state.CurrentPlaylist?.Id ?? Guid.Empty;
            }

            await SendToClientAsync(clientId, "Load", currentSong.Id, currentPlaylistId);
            await SendToClientAsync(clientId, "Play");
        }
    }

    public async Task RestartOrPrevious(Guid clientId)
    {
        Song? currentSong = GetCurrentSong(clientId);
        
        if (currentSong != null)
        {
            ClientMusicState state = GetOrCreateState(clientId);
            double positionSeconds = EstimatePositionSeconds(state, DateTime.UtcNow);

            if (positionSeconds > 10)
            {
                await Restart(clientId);

                return;
            }
        }

        await Previous(clientId);
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

        OnShuffleStateChanged?.Invoke(clientId, isEnabled);

        return Task.FromResult(isEnabled);
    }

    /// <summary>
    /// Advances the repeat setting one step around Off -> All -> One -> Off.
    /// </summary>
    public Task<RepeatMode> CycleRepeatMode(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        RepeatMode mode;

        lock (_lock)
        {
            state.RepeatMode = state.RepeatMode switch
            {
                RepeatMode.Off => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.Off
            };

            mode = state.RepeatMode;
        }

        return Task.FromResult(mode);
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

    public async Task SetVolume(Guid clientId, int volume)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        int normalizedVolume = Math.Clamp(volume, 0, 100);

        lock (_lock)
        {
            state.CurrentVolume = normalizedVolume;
        }

        await SendToClientAsync(clientId, "SetVolume", normalizedVolume);
    }

    public int GetCurrentVolume(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            return state.CurrentVolume;
        }
    }

    public bool GetCurrentShuffleState(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            return state.IsShuffleEnabled;
        }
    }

    public RepeatMode GetCurrentRepeatMode(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            return state.RepeatMode;
        }
    }

    public int GetCurrentSongTime(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        DateTime nowUtc = DateTime.UtcNow;

        lock (_lock)
        {
            return (int)EstimatePositionSeconds(state, nowUtc);
        }
    }

    public int GetCurrentSongLength(Guid clientId)
    {
        ClientMusicState state = GetOrCreateState(clientId);
        lock (_lock)
        {
            return (int)(state.CurrentSong?.DurationSeconds ?? 0);
        }
    }
}
