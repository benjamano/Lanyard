using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Lanyard.Application.Services;

/// <summary>
/// Controls music playback on kiosks over SignalR and keeps each kiosk's playback state.
/// </summary>
/// <remarks>
/// <para>
/// The server owns the queue. <c>Queue</c> holds a kiosk's songs in playlist order and never
/// changes order; <c>PlayOrder</c> is the order they play in - the identity when shuffle is off,
/// a Fisher–Yates permutation when it's on. Next and Previous step through <c>PlayOrder</c>, so a
/// shuffled playlist plays every song once before any repeats, and Previous goes back to the song
/// that actually played before rather than a random one.
/// </para>
/// <para>
/// Every field of every <see cref="ClientMusicState"/> is read and written under <see cref="_lock"/>.
/// Events are raised after the lock is released, and a subscriber that throws is logged and
/// otherwise ignored, so one broken screen can't break a kiosk's playback.
/// </para>
/// </remarks>
public class MusicPlayerService : IMusicPlayerService
{
    // How far into a song Previous restarts it instead of going back one.
    public static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(3);

    // A second "song ended" this soon after one that advanced the queue is the same end reported
    // twice (a reconnect replaying it), not a song that lasted under two seconds.
    private static readonly TimeSpan DuplicateSongEndedWindow = TimeSpan.FromSeconds(2);

    // A kiosk's position report this far from where the server thought it was counts as a jump
    // (a seek from elsewhere, or a stall) that screens need to hear about.
    private const double PositionJumpSeconds = 2.0;

    private const int UpNextCount = 5;

    private readonly IHubContext<SignalRControlHub> _hubContext;
    private readonly ISystemDbContextFactory _contextFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MusicPlayerService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();

    private readonly Dictionary<Guid, ClientMusicState> _stateByClientId = [];

    public MusicPlayerService(
        IHubContext<SignalRControlHub> hubContext,
        ISystemDbContextFactory contextFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<MusicPlayerService> logger,
        TimeProvider? timeProvider = null,
        IMusicLibraryEvents? libraryEvents = null)
    {
        _hubContext = hubContext;
        _contextFactory = contextFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (libraryEvents is not null)
        {
            libraryEvents.Changed += HandleLibraryChanged;
        }
    }

    public event Action<Guid, PlaybackState>? OnPlaybackStatusChanged;
    public event Action<Guid, Guid?>? OnSongChanged;
    public event Action<Guid, bool>? OnShuffleStateChanged;
    public event Action<Guid>? OnNowPlayingChanged;

    /// <summary>
    /// Raised after a command that changes what plays, or how loud, came from anyone but the music
    /// schedule. The schedule engine listens so it can hold off until its next part of the day.
    /// </summary>
    public event Action<Guid>? OnManualMusicChange;

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private sealed class ClientMusicState
    {
        public bool IsConnected { get; set; } = true;
        public PlaybackState CurrentState { get; set; } = PlaybackState.Stopped;
        public Song? CurrentSong { get; set; }
        public Playlist? CurrentPlaylist { get; set; }

        // Playlist order. See the class remarks.
        public List<Song> Queue { get; set; } = [];
        public List<int> PlayOrder { get; set; } = [];
        public int OrderPosition { get; set; }

        public bool IsShuffleEnabled { get; set; }
        // Defaults to looping the queue so unattended playback keeps going without someone
        // having to arm repeat on every client.
        public RepeatMode RepeatMode { get; set; } = RepeatMode.All;
        public double LastKnownPositionSeconds { get; set; }
        public DateTime LastPositionUpdateUtc { get; set; }
        public int CurrentVolume { get; set; }
        public bool IsVolumeKnown { get; set; }

        public DateTime LastAutoAdvanceUtc { get; set; } = DateTime.MinValue;

        // A playlist waiting to take over when the current song ends (see SetPendingPlaylist).
        public Guid? PendingPlaylistId { get; set; }
        public DateTime PendingSinceUtc { get; set; }

        public int? CurrentQueueIndex =>
            OrderPosition >= 0 && OrderPosition < PlayOrder.Count ? PlayOrder[OrderPosition] : null;
    }

    #region State access

    // Only for paths that are about to record something about the kiosk. Reads use TryGet so a
    // screen looking at a kiosk that has never played anything doesn't create state for it.
    private ClientMusicState GetOrCreateStateLocked(Guid clientId)
    {
        if (!_stateByClientId.TryGetValue(clientId, out ClientMusicState? state))
        {
            state = new ClientMusicState { LastPositionUpdateUtc = UtcNow };
            _stateByClientId[clientId] = state;
        }

        return state;
    }

    private T Read<T>(Guid clientId, Func<ClientMusicState, T> read, T fallback)
    {
        lock (_lock)
        {
            return _stateByClientId.TryGetValue(clientId, out ClientMusicState? state) ? read(state) : fallback;
        }
    }

    private void Raise(string name, Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A {Event} subscriber threw", name);
        }
    }

    private void RaiseNowPlaying(Guid clientId)
    {
        if (OnNowPlayingChanged is { } handler)
        {
            Raise(nameof(OnNowPlayingChanged), () => handler(clientId));
        }
    }

    private void RaiseSongChanged(Guid clientId, Guid? songId)
    {
        if (OnSongChanged is { } handler)
        {
            Raise(nameof(OnSongChanged), () => handler(clientId, songId));
        }

        RaiseNowPlaying(clientId);
    }

    private void NoteSource(Guid clientId, MusicCommandSource source)
    {
        if (source == MusicCommandSource.Manual && OnManualMusicChange is { } handler)
        {
            Raise(nameof(OnManualMusicChange), () => handler(clientId));
        }
    }

    #endregion

    #region Reads

    public MusicNowPlayingSnapshot GetSnapshot(Guid clientId)
    {
        DateTime nowUtc = UtcNow;

        lock (_lock)
        {
            return _stateByClientId.TryGetValue(clientId, out ClientMusicState? state)
                ? BuildSnapshotLocked(clientId, state, nowUtc)
                : MusicNowPlayingSnapshot.Empty(clientId);
        }
    }

    public IReadOnlyList<MusicNowPlayingSnapshot> GetSnapshots(IEnumerable<Guid> clientIds)
    {
        DateTime nowUtc = UtcNow;
        List<MusicNowPlayingSnapshot> snapshots = [];

        lock (_lock)
        {
            foreach (Guid clientId in clientIds)
            {
                snapshots.Add(_stateByClientId.TryGetValue(clientId, out ClientMusicState? state)
                    ? BuildSnapshotLocked(clientId, state, nowUtc)
                    : MusicNowPlayingSnapshot.Empty(clientId));
            }
        }

        return snapshots;
    }

    private static MusicNowPlayingSnapshot BuildSnapshotLocked(Guid clientId, ClientMusicState state, DateTime nowUtc)
    {
        List<MusicTrackInfo> upNext = [];

        if (state.PlayOrder.Count > 1)
        {
            bool wraps = state.RepeatMode != RepeatMode.Off;

            for (int step = 1; step < state.PlayOrder.Count && upNext.Count < UpNextCount; step++)
            {
                int position = state.OrderPosition + step;

                if (position >= state.PlayOrder.Count)
                {
                    if (!wraps)
                    {
                        break;
                    }

                    position -= state.PlayOrder.Count;
                }

                upNext.Add(ToTrack(state.Queue[state.PlayOrder[position]]));
            }
        }

        return new MusicNowPlayingSnapshot(
            clientId,
            state.IsConnected,
            state.CurrentState,
            state.CurrentSong is null ? null : ToTrack(state.CurrentSong),
            state.CurrentPlaylist?.Id,
            state.CurrentPlaylist?.Name,
            EstimatePositionSeconds(state, nowUtc),
            nowUtc,
            state.CurrentVolume,
            state.IsVolumeKnown,
            state.IsShuffleEnabled,
            state.RepeatMode,
            upNext,
            state.PendingPlaylistId);
    }

    private static MusicTrackInfo ToTrack(Song song) => new(song.Id, song.Name, song.AlbumName, song.DurationSeconds);

    public PlaybackState GetCurrentPlaybackState(Guid clientId) => Read(clientId, s => s.CurrentState, PlaybackState.Stopped);

    public Song? GetCurrentSong(Guid clientId) => Read(clientId, s => s.CurrentSong, null);

    public Playlist? GetCurrentPlaylist(Guid clientId) => Read(clientId, s => s.CurrentPlaylist, null);

    public bool GetShuffleEnabled(Guid clientId) => Read(clientId, s => s.IsShuffleEnabled, false);

    public bool GetCurrentShuffleState(Guid clientId) => GetShuffleEnabled(clientId);

    public RepeatMode GetCurrentRepeatMode(Guid clientId) => Read(clientId, s => s.RepeatMode, RepeatMode.All);

    public int GetCurrentVolume(Guid clientId) => Read(clientId, s => s.CurrentVolume, 0);

    public bool IsVolumeKnown(Guid clientId) => Read(clientId, s => s.IsVolumeKnown, false);

    public double GetEstimatedPositionSeconds(Guid clientId)
    {
        DateTime nowUtc = UtcNow;
        return Read(clientId, s => EstimatePositionSeconds(s, nowUtc), 0);
    }

    public int GetCurrentSongTime(Guid clientId) => (int)GetEstimatedPositionSeconds(clientId);

    public int GetCurrentSongLength(Guid clientId) => Read(clientId, s => (int)(s.CurrentSong?.DurationSeconds ?? 0), 0);

    public (Guid PlaylistId, DateTime SinceUtc)? GetPendingPlaylist(Guid clientId) =>
        Read<(Guid, DateTime)?>(clientId, s => s.PendingPlaylistId is Guid id ? (id, s.PendingSinceUtc) : null, null);

    private static double EstimatePositionSeconds(ClientMusicState state, DateTime nowUtc)
    {
        double positionSeconds = state.LastKnownPositionSeconds;

        if (state.CurrentState == PlaybackState.Playing)
        {
            positionSeconds += Math.Max(0, (nowUtc - state.LastPositionUpdateUtc).TotalSeconds);
        }

        if (state.CurrentSong is not null && state.CurrentSong.DurationSeconds > 0)
        {
            return Math.Clamp(positionSeconds, 0, state.CurrentSong.DurationSeconds);
        }

        return Math.Max(0, positionSeconds);
    }

    #endregion

    #region Reports from the kiosk

    public void MarkClientConnected(Guid clientId)
    {
        bool changed;

        lock (_lock)
        {
            // A kiosk seen for the first time since the server started is news too.
            bool isNew = !_stateByClientId.ContainsKey(clientId);
            ClientMusicState state = GetOrCreateStateLocked(clientId);
            changed = isNew || !state.IsConnected;
            state.IsConnected = true;
        }

        if (changed)
        {
            RaiseNowPlaying(clientId);
        }
    }

    /// <summary>
    /// Keeps what the server knew (shuffle, repeat, a queued playlist) for when the kiosk comes
    /// back - a dropped Wi-Fi connection shouldn't reset them - and tells screens it's offline.
    /// </summary>
    public void MarkClientDisconnected(Guid clientId)
    {
        bool changed;

        lock (_lock)
        {
            if (!_stateByClientId.TryGetValue(clientId, out ClientMusicState? state))
            {
                return;
            }

            changed = state.IsConnected;
            state.IsConnected = false;
        }

        if (changed)
        {
            RaiseNowPlaying(clientId);
        }
    }

    public async Task<Result<bool>> SetCurrentPlaylist(Guid clientId, Guid playlistId)
    {
        Playlist? playlist = await LoadPlaylistAsync(playlistId);
        bool changed;

        lock (_lock)
        {
            ClientMusicState state = GetOrCreateStateLocked(clientId);
            changed = state.CurrentPlaylist?.Id != playlist?.Id;
            state.CurrentPlaylist = playlist;
        }

        if (changed)
        {
            RaiseNowPlaying(clientId);
        }

        return Result<bool>.Ok(true);
    }

    public void UpdatePlaybackState(Guid clientId, PlaybackState state)
    {
        bool changed;
        DateTime nowUtc = UtcNow;

        lock (_lock)
        {
            ClientMusicState clientState = GetOrCreateStateLocked(clientId);
            clientState.IsConnected = true;
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
            if (OnPlaybackStatusChanged is { } handler)
            {
                Raise(nameof(OnPlaybackStatusChanged), () => handler(clientId, state));
            }

            RaiseNowPlaying(clientId);
        }
    }

    /// <summary>
    /// Anchors the position estimate to a ground-truth reading from the client's audio reader.
    /// Clients report every second while playing (and immediately after play/seek), which keeps
    /// <see cref="GetEstimatedPositionSeconds"/> accurate enough for beat-synced features.
    /// Screens are only told when the reading is well off the estimate.
    /// </summary>
    public void UpdatePlaybackPosition(Guid clientId, double positionSeconds)
    {
        bool jumped;
        DateTime nowUtc = UtcNow;

        lock (_lock)
        {
            ClientMusicState state = GetOrCreateStateLocked(clientId);
            double reported = Math.Max(0, positionSeconds);

            jumped = Math.Abs(EstimatePositionSeconds(state, nowUtc) - reported) > PositionJumpSeconds;

            state.LastKnownPositionSeconds = reported;
            state.LastPositionUpdateUtc = nowUtc;
        }

        if (jumped)
        {
            RaiseNowPlaying(clientId);
        }
    }

    public async Task UpdateCurrentSongAsync(Guid clientId, Guid songId)
    {
        bool inQueue;

        lock (_lock)
        {
            ClientMusicState state = GetOrCreateStateLocked(clientId);
            inQueue = TrySetCurrentFromQueueLocked(state, songId) || songId == Guid.Empty;

            if (songId == Guid.Empty)
            {
                // The kiosk is playing something it found offline, or nothing.
                state.CurrentSong = null;
            }

            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = UtcNow;
        }

        if (!inQueue)
        {
            // Not something the server queued (e.g. the server restarted and hasn't heard the
            // queue yet): look it up, so screens can still show what's playing.
            Song? song = await LoadSongAsync(songId);

            lock (_lock)
            {
                ClientMusicState state = GetOrCreateStateLocked(clientId);

                if (!TrySetCurrentFromQueueLocked(state, songId))
                {
                    state.CurrentSong = song;
                }
            }
        }

        RaiseSongChanged(clientId, songId == Guid.Empty ? null : songId);
    }

    private static bool TrySetCurrentFromQueueLocked(ClientMusicState state, Guid songId)
    {
        int queueIndex = state.Queue.FindIndex(x => x.Id == songId);
        if (queueIndex < 0)
        {
            return false;
        }

        state.CurrentSong = state.Queue[queueIndex];

        int orderPosition = state.PlayOrder.IndexOf(queueIndex);
        if (orderPosition >= 0)
        {
            state.OrderPosition = orderPosition;
        }

        return true;
    }

    /// <summary>
    /// The kiosk telling the server what it has queued - it does this when it reconnects. If the
    /// server already has those songs queued nothing changes: its order, place and shuffle stand.
    /// Otherwise (typically the server restarted) the kiosk's queue is taken as it is, in its order,
    /// keeping the current song if it's in it. This never picks a current song itself; that comes
    /// from the kiosk's "now playing" report.
    /// </summary>
    public async Task SetQueue(Guid clientId, List<Guid> songIds, Guid? playlistId)
    {
        List<Guid> distinctIds = songIds.Where(x => x != Guid.Empty).Distinct().ToList();

        lock (_lock)
        {
            if (_stateByClientId.TryGetValue(clientId, out ClientMusicState? existing)
                && existing.Queue.Count == distinctIds.Count
                && existing.Queue.Select(x => x.Id).ToHashSet().SetEquals(distinctIds))
            {
                return;
            }
        }

        await using ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();

        Dictionary<Guid, Song> byId = await context.Songs
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => distinctIds.Contains(x.Id) && x.IsActive)
            .ToDictionaryAsync(x => x.Id);

        List<Song> songs = distinctIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();

        Playlist? playlist = playlistId is Guid id && id != Guid.Empty ? await LoadPlaylistAsync(id) : null;

        lock (_lock)
        {
            ClientMusicState state = GetOrCreateStateLocked(clientId);
            int currentIndex = state.CurrentSong is null ? -1 : songs.FindIndex(x => x.Id == state.CurrentSong.Id);

            state.Queue = songs;
            state.CurrentPlaylist = playlist ?? state.CurrentPlaylist;
            BuildPlayOrderLocked(state, Math.Max(0, currentIndex));

            if (currentIndex >= 0)
            {
                state.CurrentSong = songs[currentIndex];
            }
        }

        RaiseNowPlaying(clientId);
    }

    /// <summary>
    /// Called when a client reports that its track finished on its own. Advances the queue via
    /// the same repeat/shuffle rules the Next button uses; with repeat-one that resolves back to
    /// the current track, which replays it. Stops quietly at the end of a non-repeating queue.
    /// </summary>
    public async Task HandleSongEndedAsync(Guid clientId)
    {
        Guid? pendingPlaylistId;
        DateTime nowUtc = UtcNow;

        lock (_lock)
        {
            if (!_stateByClientId.TryGetValue(clientId, out ClientMusicState? state))
            {
                _logger.LogWarning("Client {ClientId} reported song ended but the server has nothing queued for it", clientId);
                return;
            }

            if (nowUtc - state.LastAutoAdvanceUtc < DuplicateSongEndedWindow)
            {
                _logger.LogInformation("Client {ClientId} reported the same song ending twice; ignoring the repeat", clientId);
                return;
            }

            state.LastAutoAdvanceUtc = nowUtc;
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = nowUtc;
            pendingPlaylistId = state.PendingPlaylistId;
        }

        if (pendingPlaylistId is Guid pending)
        {
            _logger.LogInformation("Client {ClientId} switching to queued playlist {PlaylistId} at the end of the song", clientId, pending);

            Result<bool> switched = await Play(clientId, pending, MusicCommandSource.Schedule);
            if (switched.IsSuccess)
            {
                return;
            }

            // Carry on with the current queue rather than fall silent.
            _logger.LogWarning("Client {ClientId} couldn't switch to playlist {PlaylistId}: {Error}", clientId, pending, switched.Error);
        }

        Result<bool> advanced = await AdvanceAndPlayAsync(clientId, isAutomaticAdvance: true);
        if (!advanced.IsSuccess)
        {
            _logger.LogInformation("Client {ClientId} stopped after its song ended: {Reason}", clientId, advanced.Error);
        }
    }

    /// <summary>
    /// Records a volume the client reported about itself. Unlike <see cref="SetVolume"/> this
    /// doesn't send anything back, so a report can't echo into a second command.
    /// </summary>
    public void ReportVolumeFromClient(Guid clientId, int volume)
    {
        bool changed;

        lock (_lock)
        {
            ClientMusicState state = GetOrCreateStateLocked(clientId);
            int normalized = Math.Clamp(volume, 0, 100);
            changed = state.CurrentVolume != normalized || !state.IsVolumeKnown;
            state.CurrentVolume = normalized;
            state.IsVolumeKnown = true;
        }

        if (changed)
        {
            RaiseNowPlaying(clientId);
        }
    }

    #endregion

    #region Queue building

    // PlayOrder for the current queue, with the song at queueIndex playing first when shuffled.
    private static void BuildPlayOrderLocked(ClientMusicState state, int startQueueIndex)
    {
        int count = state.Queue.Count;
        startQueueIndex = count == 0 ? 0 : Math.Clamp(startQueueIndex, 0, count - 1);

        List<int> order = Enumerable.Range(0, count).ToList();

        if (state.IsShuffleEnabled && count > 1)
        {
            order.Remove(startQueueIndex);
            Shuffle(order);
            order.Insert(0, startQueueIndex);
            state.OrderPosition = 0;
        }
        else
        {
            state.OrderPosition = startQueueIndex;
        }

        state.PlayOrder = order;
    }

    private static void Shuffle(List<int> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <param name="isAutomaticAdvance">
    /// True when the current track ended by itself, false when the user pressed Next. Only an
    /// automatic advance honours <see cref="RepeatMode.One"/> - an explicit skip means the user
    /// wants to move on, so it advances regardless.
    /// </param>
    private static bool TryMoveNextLocked(ClientMusicState state, bool isAutomaticAdvance)
    {
        if (state.PlayOrder.Count == 0)
        {
            return false;
        }

        if (isAutomaticAdvance && state.RepeatMode == RepeatMode.One)
        {
            return true;
        }

        int next = state.OrderPosition + 1;

        if (next >= state.PlayOrder.Count)
        {
            // Any repeat mode keeps the queue circular; only Off runs off the end and stops.
            if (state.RepeatMode == RepeatMode.Off)
            {
                return false;
            }

            if (state.IsShuffleEnabled && state.PlayOrder.Count > 2)
            {
                // A fresh shuffle for the next lap, not starting with the song that just played.
                int last = state.PlayOrder[^1];
                List<int> order = Enumerable.Range(0, state.Queue.Count).Where(x => x != last).ToList();
                Shuffle(order);
                order.Add(last);
                state.PlayOrder = order;
            }

            next = 0;
        }

        state.OrderPosition = next;
        return true;
    }

    private static bool TryMovePreviousLocked(ClientMusicState state)
    {
        if (state.PlayOrder.Count == 0)
        {
            return false;
        }

        int previous = state.OrderPosition - 1;

        if (previous < 0)
        {
            if (state.RepeatMode == RepeatMode.Off)
            {
                return false;
            }

            previous = state.PlayOrder.Count - 1;
        }

        state.OrderPosition = previous;
        return true;
    }

    // The queue as the kiosk wants it: a song -> playlist map in play order starting at the current
    // song. The kiosk loads the first entry and pre-caches the next, so this order matters.
    private static Dictionary<Guid, Guid> BuildKioskQueueLocked(ClientMusicState state)
    {
        Guid playlistId = state.CurrentPlaylist?.Id ?? Guid.Empty;
        Dictionary<Guid, Guid> queue = [];

        for (int step = 0; step < state.PlayOrder.Count; step++)
        {
            int position = (state.OrderPosition + step) % state.PlayOrder.Count;
            queue.TryAdd(state.Queue[state.PlayOrder[position]].Id, playlistId);
        }

        return queue;
    }

    private async Task<Playlist?> LoadPlaylistAsync(Guid playlistId)
    {
        await using ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();

        return await context.Playlists
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.Id == playlistId)
            .Select(x => new Playlist { Id = x.Id, Name = x.Name, CompanyId = x.CompanyId, CreateDate = x.CreateDate, DeleteDate = x.DeleteDate })
            .FirstOrDefaultAsync();
    }

    private async Task<Song?> LoadSongAsync(Guid songId)
    {
        await using ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();

        return await context.Songs
            .AsNoTracking()
            .TagWithCallSite()
            .FirstOrDefaultAsync(x => x.Id == songId && x.IsActive);
    }

    private async Task<List<Song>> LoadPlaylistSongsAsync(Guid playlistId)
    {
        await using ApplicationDbContext context = await _contextFactory.CreateDbContextAsync();

        return await context.PlaylistSongMembers
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.PlaylistId == playlistId && x.DeleteDate == null && x.Song!.IsActive)
            .OrderBy(x => x.Position)
            .ThenByDescending(x => x.Song!.CreateDate)
            .Select(x => x.Song!)
            .ToListAsync();
    }

    /// <summary>
    /// Replaces the kiosk's queue and sends it, then plays. Fails without touching the kiosk when
    /// there's nothing to play: the kiosk can't load an empty queue, and older kiosks crash on one.
    /// </summary>
    private async Task<Result<bool>> LoadQueueAndPlayAsync(
        Guid clientId, List<Song> songs, Playlist? playlist, int startQueueIndex, bool? shuffle, MusicCommandSource source)
    {
        if (songs.Count == 0)
        {
            _logger.LogWarning("Not loading playlist {PlaylistId} on client {ClientId}: it has no active songs", playlist?.Id, clientId);
            return Result<bool>.Fail("This playlist has no songs to play.");
        }

        Dictionary<Guid, Guid> kioskQueue;
        Song first;
        bool shuffleChanged = false;
        bool shuffleOn;

        lock (_lock)
        {
            ClientMusicState state = GetOrCreateStateLocked(clientId);

            if (shuffle is bool wanted && state.IsShuffleEnabled != wanted)
            {
                state.IsShuffleEnabled = wanted;
                shuffleChanged = true;
            }

            shuffleOn = state.IsShuffleEnabled;
            state.PendingPlaylistId = null;
            state.Queue = songs;
            state.CurrentPlaylist = playlist;
            BuildPlayOrderLocked(state, startQueueIndex);

            first = state.Queue[state.CurrentQueueIndex!.Value];
            state.CurrentSong = first;
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = UtcNow;

            kioskQueue = BuildKioskQueueLocked(state);
        }

        if (shuffleChanged && OnShuffleStateChanged is { } shuffleHandler)
        {
            Raise(nameof(OnShuffleStateChanged), () => shuffleHandler(clientId, shuffleOn));
        }

        RaiseSongChanged(clientId, first.Id);

        // The kiosk's LoadPlaylist loads the first song itself, so no separate Load is needed.
        Result<bool> sent = await SendToClientAsync(clientId, ("LoadPlaylist", [kioskQueue]), ("Play", []));
        if (sent.IsSuccess)
        {
            NoteSource(clientId, source);
        }

        return sent;
    }

    // Moves on one (or replays, for repeat-one at a natural end) and has the kiosk play it.
    private async Task<Result<bool>> AdvanceAndPlayAsync(Guid clientId, bool isAutomaticAdvance)
    {
        Song? song;
        Guid playlistId;

        lock (_lock)
        {
            if (!_stateByClientId.TryGetValue(clientId, out ClientMusicState? state) || state.PlayOrder.Count == 0)
            {
                return Result<bool>.Fail("Nothing is queued on this kiosk.");
            }

            // Repeat-one needs a song to repeat; if the playing song was taken out of the playlist
            // there isn't one in the queue, so move on instead.
            bool replay = isAutomaticAdvance && state.CurrentQueueIndex is not null;

            if (!TryMoveNextLocked(state, replay))
            {
                return Result<bool>.Fail("That was the end of the queue.");
            }

            song = state.Queue[state.CurrentQueueIndex!.Value];
            state.CurrentSong = song;
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = UtcNow;
            playlistId = state.CurrentPlaylist?.Id ?? Guid.Empty;
        }

        _logger.LogInformation("Client {ClientId} moving to song {SongId}", clientId, song.Id);

        RaiseSongChanged(clientId, song.Id);

        return await SendToClientAsync(clientId, ("Load", [song.Id, playlistId]), ("Play", []));
    }

    #endregion

    #region Playlist edits

    private void HandleLibraryChanged(int companyId, Guid? playlistId)
    {
        if (playlistId is not Guid id)
        {
            return;
        }

        bool anyPlaying;
        lock (_lock)
        {
            anyPlaying = _stateByClientId.Values.Any(x => x.CurrentPlaylist?.Id == id);
        }

        if (anyPlaying)
        {
            // Raised from inside the edit; the reload runs on its own and logs its own failures.
            _ = RefreshPlaylistQueuesAsync(id);
        }
    }

    /// <summary>
    /// After a playlist's songs are added, removed or reordered, brings every kiosk playing it up
    /// to date without interrupting the song playing. Only the server's queue changes - it decides
    /// what plays next - so nothing is sent to the kiosk. A song that was taken out keeps playing
    /// to its end and the queue carries on from where it was.
    /// </summary>
    public async Task RefreshPlaylistQueuesAsync(Guid playlistId)
    {
        try
        {
            List<Song> songs = await LoadPlaylistSongsAsync(playlistId);
            List<Guid> refreshed = [];

            lock (_lock)
            {
                foreach ((Guid clientId, ClientMusicState state) in _stateByClientId)
                {
                    if (state.CurrentPlaylist?.Id != playlistId)
                    {
                        continue;
                    }

                    int oldIndex = state.CurrentQueueIndex ?? 0;
                    int currentIndex = state.CurrentSong is null ? -1 : songs.FindIndex(x => x.Id == state.CurrentSong.Id);

                    state.Queue = songs;

                    if (currentIndex >= 0)
                    {
                        BuildPlayOrderLocked(state, currentIndex);
                    }
                    else
                    {
                        // The playing song is gone: start the order at whatever now sits where it
                        // was, and step back one so Next lands on it.
                        BuildPlayOrderLocked(state, Math.Min(oldIndex, Math.Max(0, songs.Count - 1)));
                        state.OrderPosition -= 1;
                    }

                    refreshed.Add(clientId);
                }
            }

            foreach (Guid clientId in refreshed)
            {
                RaiseNowPlaying(clientId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh the queues playing playlist {PlaylistId}", playlistId);
        }
    }

    #endregion

    #region Commands

    public Task<Result<bool>> Play(Guid clientId, MusicCommandSource source = MusicCommandSource.Manual) =>
        SendCommandAsync(clientId, "Play", [], source);

    public async Task<Result<bool>> Play(Guid clientId, Guid playlistId, MusicCommandSource source = MusicCommandSource.Manual)
    {
        Playlist? playlist = await LoadPlaylistAsync(playlistId);
        if (playlist is null || playlist.DeleteDate is not null)
        {
            return Result<bool>.Fail("That playlist no longer exists.");
        }

        List<Song> songs = await LoadPlaylistSongsAsync(playlistId);

        // Shuffled playlists start on a random song; otherwise from the top.
        int start = GetShuffleEnabled(clientId) && songs.Count > 0 ? Random.Shared.Next(songs.Count) : 0;

        return await LoadQueueAndPlayAsync(clientId, songs, playlist, start, shuffle: null, source);
    }

    public async Task<Result<bool>> ShufflePlay(Guid clientId, Guid playlistId, MusicCommandSource source = MusicCommandSource.Manual)
    {
        Playlist? playlist = await LoadPlaylistAsync(playlistId);
        if (playlist is null || playlist.DeleteDate is not null)
        {
            return Result<bool>.Fail("That playlist no longer exists.");
        }

        List<Song> songs = await LoadPlaylistSongsAsync(playlistId);
        int start = songs.Count > 0 ? Random.Shared.Next(songs.Count) : 0;

        return await LoadQueueAndPlayAsync(clientId, songs, playlist, start, shuffle: true, source);
    }

    public async Task<Result<bool>> PlaySong(Guid clientId, Guid songId, Guid? playlistId, MusicCommandSource source = MusicCommandSource.Manual)
    {
        Guid? wantedPlaylist = playlistId == Guid.Empty ? null : playlistId;

        bool isCurrent = Read(clientId,
            s => s.CurrentSong?.Id == songId && s.CurrentPlaylist?.Id == wantedPlaylist && s.CurrentQueueIndex is not null,
            false);

        if (isCurrent)
        {
            // Clicking the song that's already loaded resumes it rather than restarting it.
            return await Play(clientId, source);
        }

        if (wantedPlaylist is not Guid id)
        {
            Song? song = await LoadSongAsync(songId);
            if (song is null)
            {
                return Result<bool>.Fail("That song is no longer available.");
            }

            return await LoadQueueAndPlayAsync(clientId, [song], playlist: null, 0, shuffle: null, source);
        }

        Playlist? playlist = await LoadPlaylistAsync(id);
        if (playlist is null || playlist.DeleteDate is not null)
        {
            return Result<bool>.Fail("That playlist no longer exists.");
        }

        List<Song> songs = await LoadPlaylistSongsAsync(id);
        int start = songs.FindIndex(x => x.Id == songId);
        if (start < 0)
        {
            return Result<bool>.Fail("That song is no longer in this playlist.");
        }

        return await LoadQueueAndPlayAsync(clientId, songs, playlist, start, shuffle: null, source);
    }

    /// <summary>
    /// Queues <paramref name="playlistId"/> to start when the current song ends, rather than
    /// cutting it off. Null cancels. Anything that picks new music directly cancels it too.
    /// </summary>
    public void SetPendingPlaylist(Guid clientId, Guid? playlistId)
    {
        bool changed;

        lock (_lock)
        {
            if (playlistId is null && !_stateByClientId.ContainsKey(clientId))
            {
                return;
            }

            ClientMusicState state = GetOrCreateStateLocked(clientId);
            changed = state.PendingPlaylistId != playlistId;
            state.PendingPlaylistId = playlistId;
            state.PendingSinceUtc = UtcNow;
        }

        if (changed)
        {
            RaiseNowPlaying(clientId);
        }
    }

    public Task<Result<bool>> Pause(Guid clientId, MusicCommandSource source = MusicCommandSource.Manual) =>
        SendCommandAsync(clientId, "Pause", [], source);

    public Task<Result<bool>> TogglePlay(Guid clientId, MusicCommandSource source = MusicCommandSource.Manual) =>
        GetCurrentPlaybackState(clientId) == PlaybackState.Playing
            ? Pause(clientId, source)
            : Play(clientId, source);

    public async Task<Result<bool>> Stop(Guid clientId, MusicCommandSource source = MusicCommandSource.Manual)
    {
        lock (_lock)
        {
            if (_stateByClientId.TryGetValue(clientId, out ClientMusicState? state))
            {
                state.PendingPlaylistId = null;
                state.LastKnownPositionSeconds = 0;
                state.LastPositionUpdateUtc = UtcNow;
            }
        }

        return await SendCommandAsync(clientId, "Stop", [], source);
    }

    // Skipping isn't a change by hand for the schedule: it keeps playing what the schedule chose.
    public Task<Result<bool>> Next(Guid clientId) => AdvanceAndPlayAsync(clientId, isAutomaticAdvance: false);

    public async Task<Result<bool>> Previous(Guid clientId)
    {
        Song song;
        Guid playlistId;

        lock (_lock)
        {
            if (!_stateByClientId.TryGetValue(clientId, out ClientMusicState? state) || state.PlayOrder.Count == 0)
            {
                return Result<bool>.Fail("Nothing is queued on this kiosk.");
            }

            if (!TryMovePreviousLocked(state))
            {
                return Result<bool>.Fail("That was the start of the queue.");
            }

            song = state.Queue[state.CurrentQueueIndex!.Value];
            state.CurrentSong = song;
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = UtcNow;
            playlistId = state.CurrentPlaylist?.Id ?? Guid.Empty;
        }

        RaiseSongChanged(clientId, song.Id);

        return await SendToClientAsync(clientId, ("Load", [song.Id, playlistId]), ("Play", []));
    }

    public async Task<Result<bool>> Restart(Guid clientId)
    {
        Guid songId;
        Guid playlistId;

        lock (_lock)
        {
            if (!_stateByClientId.TryGetValue(clientId, out ClientMusicState? state) || state.CurrentSong is null)
            {
                return Result<bool>.Fail("Nothing is playing on this kiosk.");
            }

            songId = state.CurrentSong.Id;
            playlistId = state.CurrentPlaylist?.Id ?? Guid.Empty;
            state.LastKnownPositionSeconds = 0;
            state.LastPositionUpdateUtc = UtcNow;
        }

        RaiseNowPlaying(clientId);

        return await SendToClientAsync(clientId, ("Load", [songId, playlistId]), ("Play", []));
    }

    public Task<Result<bool>> RestartOrPrevious(Guid clientId)
    {
        bool restart = Read(clientId,
            s => s.CurrentSong is not null && EstimatePositionSeconds(s, UtcNow) > RestartThreshold.TotalSeconds,
            false);

        return restart ? Restart(clientId) : Previous(clientId);
    }

    /// <summary>
    /// Turning shuffle on shuffles everything after the current song; turning it off goes back to
    /// playlist order from the current song. Either way the song playing carries on.
    /// </summary>
    public Task<bool> ToggleShuffle(Guid clientId)
    {
        bool isEnabled;

        lock (_lock)
        {
            ClientMusicState state = GetOrCreateStateLocked(clientId);
            state.IsShuffleEnabled = !state.IsShuffleEnabled;
            isEnabled = state.IsShuffleEnabled;

            if (state.CurrentQueueIndex is int current)
            {
                BuildPlayOrderLocked(state, current);
            }
        }

        if (OnShuffleStateChanged is { } handler)
        {
            Raise(nameof(OnShuffleStateChanged), () => handler(clientId, isEnabled));
        }

        RaiseNowPlaying(clientId);

        return Task.FromResult(isEnabled);
    }

    /// <summary>
    /// Advances the repeat setting one step around Off -> All -> One -> Off.
    /// </summary>
    public Task<RepeatMode> CycleRepeatMode(Guid clientId)
    {
        RepeatMode mode;

        lock (_lock)
        {
            ClientMusicState state = GetOrCreateStateLocked(clientId);
            state.RepeatMode = state.RepeatMode switch
            {
                RepeatMode.Off => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.Off
            };

            mode = state.RepeatMode;
        }

        RaiseNowPlaying(clientId);

        return Task.FromResult(mode);
    }

    public async Task<Result<bool>> Seek(Guid clientId, double positionSeconds)
    {
        double normalizedSeconds;

        lock (_lock)
        {
            ClientMusicState state = GetOrCreateStateLocked(clientId);
            double duration = state.CurrentSong?.DurationSeconds ?? 0;
            normalizedSeconds = duration > 0 ? Math.Clamp(positionSeconds, 0, duration) : Math.Max(0, positionSeconds);
            state.LastKnownPositionSeconds = normalizedSeconds;
            state.LastPositionUpdateUtc = UtcNow;
        }

        RaiseNowPlaying(clientId);

        return await SendToClientAsync(clientId, ("Seek", [normalizedSeconds]));
    }

    public async Task GetCachedSongsAsync(Guid clientId)
    {
        await SendToClientAsync(clientId, ("GetCachedSongs", []));
    }

    public async Task<Result<bool>> SetVolume(Guid clientId, int volume, MusicCommandSource source = MusicCommandSource.Manual)
    {
        int normalizedVolume = Math.Clamp(volume, 0, 100);

        lock (_lock)
        {
            ClientMusicState state = GetOrCreateStateLocked(clientId);
            state.CurrentVolume = normalizedVolume;
            state.IsVolumeKnown = true;
        }

        RaiseNowPlaying(clientId);

        return await SendCommandAsync(clientId, "SetVolume", [normalizedVolume], source);
    }

    #endregion

    #region Sending

    private async Task<Result<bool>> SendCommandAsync(Guid clientId, string method, object?[] args, MusicCommandSource source)
    {
        Result<bool> sent = await SendToClientAsync(clientId, (method, args));

        if (sent.IsSuccess)
        {
            NoteSource(clientId, source);
        }

        return sent;
    }

    private async Task<string?> GetClientConnectionIdAsync(Guid clientId)
    {
        // MusicPlayerService is a singleton, so IClientService (scoped) can't be constructor-injected -
        // resolve it per call via a short-lived scope, same pattern as DmxService.UpdateChannelValue.
        // This also picks up IClientService's cached connection-ID lookup instead of hitting the DB
        // on every single Play/Pause/Seek/Volume command.
        using IServiceScope scope = _scopeFactory.CreateScope();
        IClientService clientService = scope.ServiceProvider.GetRequiredService<IClientService>();

        Result<string?> result = await clientService.GetClientCurrentConnectionIdAsync(clientId);

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Data))
        {
            _logger.LogWarning("Could not resolve connection for client {ClientId}: {Error}", clientId, result.Error);
            return null;
        }

        return result.Data;
    }

    // Sends one or more messages in order, resolving the kiosk's connection once for the lot.
    private async Task<Result<bool>> SendToClientAsync(Guid clientId, params (string Method, object?[] Args)[] messages)
    {
        string? connectionId = await GetClientConnectionIdAsync(clientId);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return Result<bool>.Fail("That kiosk isn't connected.");
        }

        ISingleClientProxy proxy = _hubContext.Clients.Client(connectionId);

        foreach ((string method, object?[] args) in messages)
        {
            try
            {
                // Timestamped so a slow command can be diagnosed by comparing this against the
                // client's "Received {Method} command" log line - narrows a reported delay down to
                // either server-side dispatch (the gap before this line) or transport/client-side
                // processing (the gap after it).
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                _logger.LogInformation("Dispatching {Method} to client {ClientId} (connection {ConnectionId})", method, clientId, connectionId);

                await proxy.SendCoreAsync(method, args);

                _logger.LogInformation("Dispatched {Method} to client {ClientId} in {ElapsedMs}ms", method, clientId, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send {Method} to client {ClientId}", method, clientId);
                return Result<bool>.Fail($"Couldn't reach the kiosk: {ex.Message}");
            }
        }

        return Result<bool>.Ok(true);
    }

    #endregion
}
