using System.Collections.Concurrent;
using System.Diagnostics;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// The DMX output engine: one in-memory 512-channel universe per kiosk, plus the send path to it.
/// A singleton, and deliberately system-level - it never checks who is asking. Anything acting
/// for a signed-in user goes through the scoped <see cref="IDmxDeskService"/>, which checks the
/// kiosk belongs to the user's company first.
/// </summary>
/// <remarks>
/// Writes are coalesced per kiosk. The first write after a quiet spell is sent straight away (so a
/// scene step or a tap lands with no added latency); writes inside the following
/// <see cref="CoalesceWindow"/> only mark their channels dirty, and one flush sends the latest
/// value of each. A fader dragged at 50 events a second therefore costs at most ~40 kiosk messages
/// a second - DMX's own refresh rate - and callers never queue behind the hub.
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
    private readonly ConcurrentDictionary<string, Guid> _clientIdByConnectionId = new();

    private int _flushLoopRunning;

    private sealed class ClientDmxState(Guid clientId)
    {
        public Guid ClientId { get; } = clientId;

        // Index = channel address; slot 0 unused so addresses index directly.
        public byte[] Values { get; } = new byte[UniverseSize + 1];
        public bool[] Dirty { get; } = new bool[UniverseSize + 1];
        public int DirtyCount { get; set; }
        public long LastSendTimestamp { get; set; }

        // Serialises sends so the kiosk always receives batches in the order they were taken.
        public SemaphoreSlim SendGate { get; } = new(1, 1);

        public string? ConnectionId { get; set; }
        public bool ConnectionKnown { get; set; }
        public DateTime ConnectionLookupExpiresUtc { get; set; }

        public Action<IReadOnlyList<DmxChannel>>[] Subscribers { get; set; } = [];

        // Guards every field above except SendGate.
        public object Lock { get; } = new();
    }

    private ClientDmxState GetOrCreateState(Guid clientId) =>
        _stateByClientId.GetOrAdd(clientId, id => new ClientDmxState(id));

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

        ClientDmxState state = GetOrCreateState(clientId);
        bool sendNow;

        lock (state.Lock)
        {
            foreach (DmxChannel channel in valid)
            {
                state.Values[channel.Address] = channel.Value;

                if (!state.Dirty[channel.Address])
                {
                    state.Dirty[channel.Address] = true;
                    state.DirtyCount++;
                }
            }

            sendNow = Stopwatch.GetElapsedTime(state.LastSendTimestamp) >= CoalesceWindow;
        }

        // After the write, outside the lock, each subscriber isolated: a desk that throws can't
        // stop the kiosk send or the other desks.
        Notify(state, valid);

        if (sendNow)
        {
            await FlushClientAsync(state);
        }
        else
        {
            EnsureFlushLoop();
        }
    }

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

    /// <summary>Sends everything still waiting to be coalesced, for every kiosk. Used by the flush loop and tests.</summary>
    public async Task FlushPendingAsync()
    {
        foreach (ClientDmxState state in _stateByClientId.Values)
        {
            bool hasDirty;

            lock (state.Lock)
            {
                hasDirty = state.DirtyCount > 0;
            }

            if (hasDirty)
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
                        batch.Add(new DmxChannel { Address = address, Value = state.Values[address] });
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

    public async Task RegisterConnectionAsync(Guid clientId, string connectionId)
    {
        if (clientId == Guid.Empty || string.IsNullOrEmpty(connectionId))
        {
            return;
        }

        _clientIdByConnectionId[connectionId] = clientId;

        bool hasState = _stateByClientId.TryGetValue(clientId, out ClientDmxState? state);
        state ??= GetOrCreateState(clientId);

        lock (state.Lock)
        {
            state.ConnectionId = connectionId;
            state.ConnectionKnown = true;
        }

        // A kiosk that restarted boots to an all-zero frame. If this process has been driving it,
        // put its universe back. A fresh process has nothing to restore, so it leaves the kiosk alone.
        if (hasState)
        {
            await ResendUniverseAsync(state);
        }
    }

    private async Task ResendUniverseAsync(ClientDmxState state)
    {
        await state.SendGate.WaitAsync();

        try
        {
            List<DmxChannel> universe;

            lock (state.Lock)
            {
                universe = new List<DmxChannel>(UniverseSize);

                for (int address = 1; address <= UniverseSize; address++)
                {
                    state.Dirty[address] = false;
                    universe.Add(new DmxChannel { Address = address, Value = state.Values[address] });
                }

                state.DirtyCount = 0;
                state.LastSendTimestamp = Stopwatch.GetTimestamp();
            }

            _logger.LogInformation("Re-sent the DMX universe to client {ClientId} after it connected", state.ClientId);

            await SendToKioskAsync(state, universe);
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
        }

        Notify(state, [new DmxChannel { Address = channelAddress, Value = value }]);
    }

    public Task<Result<IEnumerable<DmxChannel>>> GetDmxChannelsAsync(Guid clientId)
    {
        try
        {
            List<DmxChannel> channels = new(UniverseSize);

            if (_stateByClientId.TryGetValue(clientId, out ClientDmxState? state))
            {
                // Copied inside the lock: a lazy enumeration escaping it raced every writer.
                lock (state.Lock)
                {
                    for (int address = 1; address <= UniverseSize; address++)
                    {
                        channels.Add(new DmxChannel { Address = address, Value = state.Values[address] });
                    }
                }
            }
            else
            {
                for (int address = 1; address <= UniverseSize; address++)
                {
                    channels.Add(new DmxChannel { Address = address, Value = 0 });
                }
            }

            return Task.FromResult(Result<IEnumerable<DmxChannel>>.Ok(channels));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX channels for client {ClientId}", clientId);

            return Task.FromResult(Result<IEnumerable<DmxChannel>>.Fail("An error occurred while retrieving DMX channels."));
        }
    }

    public IDisposable SubscribeToClient(Guid clientId, Action<IReadOnlyList<DmxChannel>> handler)
    {
        ClientDmxState state = GetOrCreateState(clientId);

        lock (state.Lock)
        {
            state.Subscribers = [.. state.Subscribers, handler];
        }

        return new Subscription(() =>
        {
            lock (state.Lock)
            {
                state.Subscribers = state.Subscribers.Where(h => h != handler).ToArray();
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

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
