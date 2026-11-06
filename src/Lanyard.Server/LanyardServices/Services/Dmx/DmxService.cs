using System.Collections.Concurrent;
using System.Diagnostics;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Lanyard.Shared.Dmx;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// The DMX output engine: one 512-channel universe per kiosk, its grand master and blackout, and
/// the send path to the kiosk. A singleton, and deliberately system-level - it never checks who is
/// asking. Anything acting for a signed-in user goes through the scoped
/// <see cref="IDmxDeskService"/>, which checks the kiosk belongs to the user's company first.
/// </summary>
/// <remarks>
/// <para>Writes are coalesced per kiosk. The first write after a quiet spell is sent straight away
/// (so a scene step or a tap lands with no added latency); writes inside the following
/// <see cref="CoalesceWindow"/> only mark their channels dirty, and one flush sends the latest
/// value of each. A fader dragged at 50 events a second therefore costs at most ~40 kiosk messages
/// a second - DMX's own refresh rate - and callers never queue behind the hub.</para>
/// <para>The universe holds the levels as programmed. What goes to the kiosk is that, with the grand
/// master and blackout applied to the dimmable channels only (see <see cref="DmxDimmableChannels"/>).
/// Subscribers (the desks) see programmed levels, so faders don't jump when the master moves.</para>
/// <para>State is loaded from <c>DmxClientOutputStates</c> the first time a kiosk is touched and
/// written back by <see cref="PersistDirtyAsync"/> (every 2 s, from DmxOutputStatePersister), so a
/// server restart or a kiosk reconnect puts the lights back.</para>
/// </remarks>
public class DmxService(ISystemDbContextFactory factory,
    IHubContext<SignalRControlHub> hubContext,
    ILogger<DmxService> logger,
    IServiceScopeFactory scopeFactory) : IDmxService, IDmxClientService
{
    public const int UniverseSize = 512;

    private readonly ISystemDbContextFactory _factory = factory;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly ILogger<DmxService> _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    /// <summary>
    /// SignalR method the kiosk listens on for a batch of channel values. The kiosk applies
    /// the whole batch to its DMX frame - it does not echo the values back.
    /// </summary>
    public const string ReceiveDmxChannelValuesMethod = "ReceiveDmxChannelValues";

    /// <summary>
    /// Single-channel method understood by kiosks older than client 1.0.40. Sent only to
    /// connections that didn't advertise batch support. Remove once every kiosk is on 1.0.40+.
    /// </summary>
    public const string LegacyReceiveDmxChannelValueMethod = "ReceiveDmxChannelValue";

    /// <summary>How long writes are gathered into one kiosk message. ~40 Hz, DMX's refresh rate.</summary>
    public TimeSpan CoalesceWindow { get; set; } = TimeSpan.FromMilliseconds(25);

    // How long "this kiosk has no connection" is believed before asking the database again.
    private static readonly TimeSpan OfflineLookupTtl = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, ClientDmxState> _stateByClientId = new();
    private readonly ConcurrentDictionary<string, byte> _legacyConnectionIds = new();
    private readonly ConcurrentDictionary<string, byte> _frameConnectionIds = new();
    private readonly ConcurrentDictionary<string, Guid> _clientIdByConnectionId = new();

    private int _flushLoopRunning;

    private sealed class ClientDmxState(Guid clientId)
    {
        public Guid ClientId { get; } = clientId;

        // Index = channel address; slot 0 unused so addresses index directly. Programmed levels.
        public byte[] Values { get; } = new byte[UniverseSize + 1];
        public bool[] Dirty { get; } = new bool[UniverseSize + 1];
        public int DirtyCount { get; set; }
        public long LastSendTimestamp { get; set; }

        public byte GrandMaster { get; set; } = 255;
        public bool IsBlackout { get; set; }

        // Which addresses master/blackout act on; all true until the kiosk's fixtures are loaded.
        public bool[] Dimmable { get; set; } = AllDimmable();

        // True once this process has levels worth restoring (loaded from the database or written).
        public bool HasUniverse { get; set; }

        // Changed since the last write-back.
        public bool PersistDirty { get; set; }

        public bool Loaded { get; set; }
        public SemaphoreSlim LoadGate { get; } = new(1, 1);

        // Serialises sends so the kiosk always receives batches in the order they were taken.
        public SemaphoreSlim SendGate { get; } = new(1, 1);

        public string? ConnectionId { get; set; }
        public bool ConnectionKnown { get; set; }
        public DateTime ConnectionLookupExpiresUtc { get; set; }

        public Action<IReadOnlyList<DmxChannel>>[] Subscribers { get; set; } = [];
        public Action<DmxOutputState>[] OutputSubscribers { get; set; } = [];

        // Guards every field above except the two gates.
        public object Lock { get; } = new();

        public byte OutputAt(int address) =>
            DmxDimmableChannels.Output(Values[address], Dimmable[address], GrandMaster, IsBlackout);

        public void MarkDirty(int address)
        {
            if (!Dirty[address])
            {
                Dirty[address] = true;
                DirtyCount++;
            }
        }
    }

    private static bool[] AllDimmable()
    {
        bool[] all = new bool[UniverseSize + 1];
        Array.Fill(all, true, 1, UniverseSize);
        return all;
    }

    private ClientDmxState GetOrCreateState(Guid clientId) =>
        _stateByClientId.GetOrAdd(clientId, id => new ClientDmxState(id));

    // First touch of a kiosk in this process: its saved universe, master/blackout and fixture patch.
    private async Task<ClientDmxState> GetLoadedStateAsync(Guid clientId)
    {
        ClientDmxState state = GetOrCreateState(clientId);

        if (state.Loaded)
        {
            return state;
        }

        await state.LoadGate.WaitAsync();

        try
        {
            if (state.Loaded)
            {
                return state;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            DmxClientOutputState? saved = await ctx.DmxClientOutputStates
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(s => s.ClientId == clientId);

            bool[] dimmable = await LoadDimmableAsync(ctx, clientId);

            lock (state.Lock)
            {
                state.Dimmable = dimmable;

                // Only if nothing has written to this kiosk while we were loading.
                if (saved != null && !state.HasUniverse)
                {
                    int count = Math.Min(saved.Values.Length, UniverseSize);
                    Array.Copy(saved.Values, 0, state.Values, 1, count);
                    state.GrandMaster = saved.GrandMaster;
                    state.IsBlackout = saved.IsBlackout;
                    state.HasUniverse = true;
                }

                state.Loaded = true;
            }
        }
        catch (Exception ex)
        {
            // Lights matter more than the saved copy: carry on from zeros/full master rather than
            // refuse to drive the kiosk. Marked loaded so a broken database isn't asked every frame.
            _logger.LogWarning(ex, "Couldn't load the saved DMX state for client {ClientId}; starting from an empty universe", clientId);

            lock (state.Lock)
            {
                state.Loaded = true;
            }
        }
        finally
        {
            state.LoadGate.Release();
        }

        return state;
    }

    private static async Task<bool[]> LoadDimmableAsync(ApplicationDbContext ctx, Guid clientId)
    {
        List<DmxFixture> fixtures = await ctx.DmxFixtures
            .AsNoTracking()
            .TagWithCallSite()
            .Where(f => f.ClientId == clientId && f.IsActive)
            .Include(f => f.FixtureMode)
                .ThenInclude(m => m!.Channels)
            .ToListAsync();

        return DmxDimmableChannels.Build(fixtures);
    }

    public Task UpdateChannelValue(Guid clientId, int channelAddress, byte value)
    {
        return UpdateChannelValuesAsync(clientId, [new DmxChannel { Address = channelAddress, Value = value }]);
    }

    public async Task UpdateChannelValuesAsync(Guid clientId, IReadOnlyList<DmxChannel> channels)
    {
        if (clientId == Guid.Empty || channels.Count == 0)
        {
            return;
        }

        List<DmxChannel> valid = Validate(clientId, channels);

        if (valid.Count == 0)
        {
            return;
        }

        ClientDmxState state = await GetLoadedStateAsync(clientId);
        bool sendNow;

        lock (state.Lock)
        {
            foreach (DmxChannel channel in valid)
            {
                state.Values[channel.Address] = channel.Value;
                state.MarkDirty(channel.Address);
            }

            state.HasUniverse = true;
            state.PersistDirty = true;
            sendNow = IsQuiet(state);
        }

        // After the write, outside the lock, each subscriber isolated: a desk that throws can't
        // stop the kiosk send or the other desks.
        Notify(state, valid);

        await SendOrScheduleAsync(state, sendNow);
    }

    private async Task SendOrScheduleAsync(ClientDmxState state, bool sendNow)
    {
        if (sendNow)
        {
            await FlushClientAsync(state);
        }
        else
        {
            EnsureFlushLoop();
        }
    }

    // Nothing sent within the coalescing window, so this write can go straight out. "Never sent"
    // is its own case: Stopwatch timestamps count from boot on Windows, so measuring from 0 would
    // hold back a kiosk's first write on a machine that booted less than a window ago.
    private bool IsQuiet(ClientDmxState state) =>
        state.LastSendTimestamp == 0 || Stopwatch.GetElapsedTime(state.LastSendTimestamp) >= CoalesceWindow;

    private List<DmxChannel> Validate(Guid clientId, IReadOnlyList<DmxChannel> channels)
    {
        List<DmxChannel> valid = new(channels.Count);

        foreach (DmxChannel channel in channels)
        {
            if (channel.Address is >= 1 and <= UniverseSize)
            {
                valid.Add(channel);
            }
            else
            {
                _logger.LogWarning("Ignored DMX write to channel {ChannelAddress} for client {ClientId}: channels are 1-512", channel.Address, clientId);
            }
        }

        return valid;
    }

    public async Task SetGrandMasterAsync(Guid clientId, byte level)
    {
        if (clientId == Guid.Empty)
        {
            return;
        }

        ClientDmxState state = await GetLoadedStateAsync(clientId);

        await ChangeOutputAsync(state, s =>
        {
            if (s.GrandMaster == level)
            {
                return false;
            }

            s.GrandMaster = level;
            return true;
        });
    }

    public async Task SetBlackoutAsync(Guid clientId, bool isBlackout)
    {
        if (clientId == Guid.Empty)
        {
            return;
        }

        ClientDmxState state = await GetLoadedStateAsync(clientId);

        await ChangeOutputAsync(state, s =>
        {
            if (s.IsBlackout == isBlackout)
            {
                return false;
            }

            s.IsBlackout = isBlackout;
            return true;
        });
    }

    // Master/blackout changed: every dimmable channel's output may have moved, so send them all
    // (one coalesced batch), tell the desks, and remember it.
    private async Task ChangeOutputAsync(ClientDmxState state, Func<ClientDmxState, bool> change)
    {
        DmxOutputState output;
        bool sendNow;

        lock (state.Lock)
        {
            if (!change(state))
            {
                return;
            }

            for (int address = 1; address <= UniverseSize; address++)
            {
                if (state.Dimmable[address])
                {
                    state.MarkDirty(address);
                }
            }

            state.HasUniverse = true;
            state.PersistDirty = true;
            output = new DmxOutputState(state.GrandMaster, state.IsBlackout);
            sendNow = IsQuiet(state);
        }

        _logger.LogInformation("DMX output for client {ClientId}: master {GrandMaster}/255, blackout {IsBlackout}", state.ClientId, output.GrandMaster, output.IsBlackout);

        NotifyOutput(state, output);

        await SendOrScheduleAsync(state, sendNow);
    }

    public async Task<DmxOutputState> GetOutputStateAsync(Guid clientId)
    {
        if (clientId == Guid.Empty)
        {
            return new DmxOutputState(255, false);
        }

        ClientDmxState state = await GetLoadedStateAsync(clientId);

        lock (state.Lock)
        {
            return new DmxOutputState(state.GrandMaster, state.IsBlackout);
        }
    }

    /// <summary>
    /// The kiosk's fixtures changed: re-read which channels master/blackout act on and re-send any
    /// whose output changes as a result.
    /// </summary>
    public async Task RefreshPatchAsync(Guid clientId)
    {
        if (!_stateByClientId.TryGetValue(clientId, out ClientDmxState? state) || !state.Loaded)
        {
            // Not loaded yet: the patch is read on first use anyway.
            return;
        }

        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();
            bool[] dimmable = await LoadDimmableAsync(ctx, clientId);
            bool sendNow;

            lock (state.Lock)
            {
                bool anyChanged = false;

                for (int address = 1; address <= UniverseSize; address++)
                {
                    if (state.Dimmable[address] != dimmable[address])
                    {
                        anyChanged = true;
                        state.MarkDirty(address);
                    }
                }

                state.Dimmable = dimmable;

                if (!anyChanged)
                {
                    return;
                }

                sendNow = IsQuiet(state);
            }

            await SendOrScheduleAsync(state, sendNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't refresh the DMX patch for client {ClientId}", clientId);
        }
    }

    /// <summary>
    /// Writes every kiosk whose universe, master or blackout changed since the last call. Run every
    /// couple of seconds by DmxOutputStatePersister, and once more on shutdown.
    /// </summary>
    public async Task PersistDirtyAsync(CancellationToken cancellationToken = default)
    {
        foreach (ClientDmxState state in _stateByClientId.Values)
        {
            byte[] values;
            byte grandMaster;
            bool isBlackout;

            lock (state.Lock)
            {
                if (!state.PersistDirty)
                {
                    continue;
                }

                values = state.Values[1..];
                grandMaster = state.GrandMaster;
                isBlackout = state.IsBlackout;
                state.PersistDirty = false;
            }

            try
            {
                await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

                DmxClientOutputState? row = await ctx.DmxClientOutputStates
                    .TagWithCallSite()
                    .FirstOrDefaultAsync(s => s.ClientId == state.ClientId, cancellationToken);

                if (row == null)
                {
                    // A kiosk deleted while its lights were on has nothing to save into.
                    bool clientExists = await ctx.Clients
                        .AsNoTracking()
                        .TagWithCallSite()
                        .AnyAsync(c => c.Id == state.ClientId, cancellationToken);

                    if (!clientExists)
                    {
                        continue;
                    }

                    row = new DmxClientOutputState { ClientId = state.ClientId };
                    ctx.DmxClientOutputStates.Add(row);
                }

                row.Values = values;
                row.GrandMaster = grandMaster;
                row.IsBlackout = isBlackout;
                row.UpdatedUtc = DateTime.UtcNow;

                await ctx.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Couldn't save the DMX state for client {ClientId}; will retry", state.ClientId);

                lock (state.Lock)
                {
                    state.PersistDirty = true;
                }
            }
        }
    }

    /// <summary>Sends everything still waiting to be coalesced, for every kiosk. Used by the flush loop and tests.</summary>
    public async Task FlushPendingAsync()
    {
        foreach (ClientDmxState state in _stateByClientId.Values)
        {
            if (HasDirty(state))
            {
                await FlushClientAsync(state);
            }
        }
    }

    private void EnsureFlushLoop()
    {
        if (Interlocked.CompareExchange(ref _flushLoopRunning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(RunFlushLoopAsync);
    }

    private async Task RunFlushLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(CoalesceWindow);

                await FlushPendingAsync();

                if (_stateByClientId.Values.Any(HasDirty))
                {
                    continue;
                }

                Interlocked.Exchange(ref _flushLoopRunning, 0);

                // A write may have landed between the check and the reset without starting a
                // loop of its own (it saw this one still running); pick it up rather than strand it.
                if (!_stateByClientId.Values.Any(HasDirty) || Interlocked.CompareExchange(ref _flushLoopRunning, 1, 0) != 0)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _flushLoopRunning, 0);
            _logger.LogError(ex, "DMX flush loop failed");
        }
    }

    private static bool HasDirty(ClientDmxState state)
    {
        lock (state.Lock)
        {
            return state.DirtyCount > 0;
        }
    }

    private async Task FlushClientAsync(ClientDmxState state)
    {
        await state.SendGate.WaitAsync();

        try
        {
            List<DmxChannel> batch;

            lock (state.Lock)
            {
                if (state.DirtyCount == 0)
                {
                    return;
                }

                batch = new List<DmxChannel>(state.DirtyCount);

                for (int address = 1; address <= UniverseSize; address++)
                {
                    if (state.Dirty[address])
                    {
                        state.Dirty[address] = false;
                        batch.Add(new DmxChannel { Address = address, Value = state.OutputAt(address) });
                    }
                }

                state.DirtyCount = 0;
                state.LastSendTimestamp = Stopwatch.GetTimestamp();
            }

            await SendToKioskAsync(state, batch);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send DMX values to client {ClientId}", state.ClientId);
        }
        finally
        {
            state.SendGate.Release();
        }
    }

    private async Task SendToKioskAsync(ClientDmxState state, IReadOnlyList<DmxChannel> batch)
    {
        string? connectionId = await GetConnectionIdAsync(state);

        if (string.IsNullOrEmpty(connectionId))
        {
            return;
        }

        ISingleClientProxy proxy = _hubContext.Clients.Client(connectionId);

        if (_legacyConnectionIds.ContainsKey(connectionId))
        {
            foreach (DmxChannel channel in batch)
            {
                await proxy.SendAsync(LegacyReceiveDmxChannelValueMethod, channel);
            }
        }
        else
        {
            await proxy.SendAsync(ReceiveDmxChannelValuesMethod, batch);
        }
    }

    // The hub tells us about connections as they come and go; the database is only asked when
    // we've not heard (e.g. the kiosk connected before this process started), and a "not
    // connected" answer is trusted for a few seconds so an offline kiosk doesn't cost a query a frame.
    private async Task<string?> GetConnectionIdAsync(ClientDmxState state)
    {
        lock (state.Lock)
        {
            if (state.ConnectionKnown && (state.ConnectionId != null || DateTime.UtcNow < state.ConnectionLookupExpiresUtc))
            {
                return state.ConnectionId;
            }
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        IClientService clientService = scope.ServiceProvider.GetRequiredService<IClientService>();

        Result<string?> result = await clientService.GetClientCurrentConnectionIdAsync(state.ClientId);
        string? connectionId = result.IsSuccess && !string.IsNullOrEmpty(result.Data) ? result.Data : null;

        lock (state.Lock)
        {
            state.ConnectionId = connectionId;
            state.ConnectionKnown = true;
            state.ConnectionLookupExpiresUtc = DateTime.UtcNow + OfflineLookupTtl;
        }

        if (connectionId != null)
        {
            _clientIdByConnectionId[connectionId] = state.ClientId;
        }

        return connectionId;
    }

    public void SetConnectionSupportsBatch(string connectionId, bool supportsBatch)
    {
        if (supportsBatch)
        {
            _legacyConnectionIds.TryRemove(connectionId, out _);
        }
        else
        {
            _legacyConnectionIds[connectionId] = 0;
        }
    }

    public void SetConnectionSupportsFrame(string connectionId, bool supportsFrame)
    {
        if (supportsFrame)
        {
            _frameConnectionIds[connectionId] = 0;
        }
        else
        {
            _frameConnectionIds.TryRemove(connectionId, out _);
        }
    }

    public async Task RegisterConnectionAsync(Guid clientId, string connectionId)
    {
        if (clientId == Guid.Empty || string.IsNullOrEmpty(connectionId))
        {
            return;
        }

        _clientIdByConnectionId[connectionId] = clientId;

        ClientDmxState state = await GetLoadedStateAsync(clientId);
        bool hasUniverse;

        lock (state.Lock)
        {
            state.ConnectionId = connectionId;
            state.ConnectionKnown = true;
            hasUniverse = state.HasUniverse;
        }

        // A kiosk that restarted boots to an all-zero frame, and a server restart used to leave the
        // two out of step. Put back whatever Lanyard last sent it - this process's, or the saved copy.
        // A kiosk Lanyard has never driven is left alone.
        if (hasUniverse)
        {
            await ResendUniverseAsync(state);
        }
    }

    private async Task ResendUniverseAsync(ClientDmxState state)
    {
        await state.SendGate.WaitAsync();

        try
        {
            byte[] frame = new byte[UniverseSize];

            lock (state.Lock)
            {
                for (int address = 1; address <= UniverseSize; address++)
                {
                    state.Dirty[address] = false;
                    frame[address - 1] = state.OutputAt(address);
                }

                state.DirtyCount = 0;
                state.LastSendTimestamp = Stopwatch.GetTimestamp();
            }

            _logger.LogInformation("Re-sending the DMX universe to client {ClientId} after it connected", state.ClientId);

            string? connectionId = await GetConnectionIdAsync(state);

            if (connectionId != null && _frameConnectionIds.ContainsKey(connectionId))
            {
                // One 512-byte message (base64 on the wire, ~700 bytes) instead of 512 objects.
                await _hubContext.Clients.Client(connectionId).SendAsync(DmxReceiveMethods.Frame, frame);
            }
            else
            {
                List<DmxChannel> universe = frame.Select((value, i) => new DmxChannel { Address = i + 1, Value = value }).ToList();

                await SendToKioskAsync(state, universe);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to re-send the DMX universe to client {ClientId}", state.ClientId);
        }
        finally
        {
            state.SendGate.Release();
        }
    }

    public void ForgetConnection(string connectionId)
    {
        _legacyConnectionIds.TryRemove(connectionId, out _);
        _frameConnectionIds.TryRemove(connectionId, out _);

        if (_clientIdByConnectionId.TryRemove(connectionId, out Guid clientId)
            && _stateByClientId.TryGetValue(clientId, out ClientDmxState? state))
        {
            lock (state.Lock)
            {
                // Only if a newer connection hasn't already replaced it.
                if (state.ConnectionId == connectionId)
                {
                    state.ConnectionId = null;
                    state.ConnectionKnown = true;
                    state.ConnectionLookupExpiresUtc = DateTime.UtcNow + OfflineLookupTtl;
                }
            }
        }
    }

    public void SetChannelValue(Guid clientId, int channelAddress, byte value)
    {
        if (clientId == Guid.Empty || channelAddress is < 1 or > UniverseSize)
        {
            return;
        }

        ClientDmxState state = GetOrCreateState(clientId);

        lock (state.Lock)
        {
            state.Values[channelAddress] = value;
            state.HasUniverse = true;
            state.PersistDirty = true;
        }

        Notify(state, [new DmxChannel { Address = channelAddress, Value = value }]);
    }

    public async Task<Result<IEnumerable<DmxChannel>>> GetDmxChannelsAsync(Guid clientId)
    {
        try
        {
            List<DmxChannel> channels = new(UniverseSize);

            if (clientId == Guid.Empty)
            {
                for (int address = 1; address <= UniverseSize; address++)
                {
                    channels.Add(new DmxChannel { Address = address, Value = 0 });
                }

                return Result<IEnumerable<DmxChannel>>.Ok(channels);
            }

            ClientDmxState state = await GetLoadedStateAsync(clientId);

            // Copied inside the lock: a lazy enumeration escaping it raced every writer.
            lock (state.Lock)
            {
                for (int address = 1; address <= UniverseSize; address++)
                {
                    channels.Add(new DmxChannel { Address = address, Value = state.Values[address] });
                }
            }

            return Result<IEnumerable<DmxChannel>>.Ok(channels);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX channels for client {ClientId}", clientId);

            return Result<IEnumerable<DmxChannel>>.Fail("An error occurred while retrieving DMX channels.");
        }
    }

    public IDisposable SubscribeToClient(Guid clientId, Action<IReadOnlyList<DmxChannel>> handler, Action<DmxOutputState>? onOutputChanged = null)
    {
        ClientDmxState state = GetOrCreateState(clientId);

        lock (state.Lock)
        {
            state.Subscribers = [.. state.Subscribers, handler];

            if (onOutputChanged != null)
            {
                state.OutputSubscribers = [.. state.OutputSubscribers, onOutputChanged];
            }
        }

        return new Subscription(() =>
        {
            lock (state.Lock)
            {
                state.Subscribers = state.Subscribers.Where(h => h != handler).ToArray();

                if (onOutputChanged != null)
                {
                    state.OutputSubscribers = state.OutputSubscribers.Where(h => h != onOutputChanged).ToArray();
                }
            }
        });
    }

    private void Notify(ClientDmxState state, IReadOnlyList<DmxChannel> channels)
    {
        Action<IReadOnlyList<DmxChannel>>[] subscribers;

        lock (state.Lock)
        {
            subscribers = state.Subscribers;
        }

        foreach (Action<IReadOnlyList<DmxChannel>> subscriber in subscribers)
        {
            try
            {
                subscriber(channels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A DMX channel subscriber for client {ClientId} threw; ignoring it", state.ClientId);
            }
        }
    }

    private void NotifyOutput(ClientDmxState state, DmxOutputState output)
    {
        Action<DmxOutputState>[] subscribers;

        lock (state.Lock)
        {
            subscribers = state.OutputSubscribers;
        }

        foreach (Action<DmxOutputState> subscriber in subscribers)
        {
            try
            {
                subscriber(output);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A DMX output subscriber for client {ClientId} threw; ignoring it", state.ClientId);
            }
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
