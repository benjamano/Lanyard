using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class DmxService(IDbContextFactory<ApplicationDbContext> factory,
    IHubContext<SignalRControlHub> hubContext,
    ILogger<DmxService> logger,
    IServiceScopeFactory scopeFactory) : IDmxService, IDmxClientService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly ILogger<DmxService> _logger = logger;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    private readonly Dictionary<Guid, ClientDmxState> _stateByClientId = [];
    private readonly object _lock = new();

    public event Action<Guid, IReadOnlyList<DmxChannel>>? OnChannelValuesChanged;

    /// <summary>
    /// SignalR method the kiosk listens on for a batch of channel values. The kiosk applies
    /// the whole batch to its DMX frame and sends one frame - it does not echo the values
    /// back (the server already knows them and raises <see cref="OnChannelValuesChanged"/>
    /// itself), which previously doubled every write into a second hub call + event.
    /// </summary>
    public const string ReceiveDmxChannelValuesMethod = "ReceiveDmxChannelValues";

    private sealed class ClientDmxState
    {
        public Dictionary<int, byte> ChannelValues { get; set; } = [];
    }

    private ClientDmxState GetOrCreateState(Guid clientId)
    {
        lock (_lock)
        {
            if (!_stateByClientId.TryGetValue(clientId, out ClientDmxState? state))
            {
                state = new()
                {
                    ChannelValues = Enumerable.Range(1, 512).ToDictionary(i => i, i => (byte)0)
                };

                _stateByClientId[clientId] = state;
            }

            return state;
        }
    }

    public Task UpdateChannelValue(Guid clientId, int channelAddress, byte value)
    {
        return UpdateChannelValuesAsync(clientId, [new DmxChannel { Address = channelAddress, Value = value }]);
    }

    public async Task UpdateChannelValuesAsync(Guid clientId, IReadOnlyList<DmxChannel> channels)
    {
        if (channels.Count == 0)
        {
            return;
        }

        // Keep the server-side universe in sync and notify subscribers (e.g. the
        // virtual desk) - previously only SetChannelValue (ingest) did this, so
        // server-originated changes were invisible to the UI and lost on reload.
        lock (_lock)
        {
            ClientDmxState state = GetOrCreateState(clientId);

            foreach (DmxChannel channel in channels)
            {
                state.ChannelValues[channel.Address] = channel.Value;
            }
        }

        // Raised outside the lock so subscribers can't deadlock against other channel writes.
        OnChannelValuesChanged?.Invoke(clientId, channels);

        // One scope + one connection lookup + one hub message per batch. A scene step used to
        // pay all three per channel (512 of each for a full-desk push), and the kiosk echoed
        // every one of them back as a hub call of its own.
        using IServiceScope scope = _scopeFactory.CreateScope();
        IClientService clientService = scope.ServiceProvider.GetRequiredService<IClientService>();

        Result<string?> clientConnectionIdGetResult = await clientService.GetClientCurrentConnectionIdAsync(clientId);

        if (clientConnectionIdGetResult.IsSuccess && !string.IsNullOrEmpty(clientConnectionIdGetResult.Data))
        {
            string connectionId = clientConnectionIdGetResult.Data;
            await _hubContext.Clients.Client(connectionId).SendAsync(ReceiveDmxChannelValuesMethod, channels);
        }
    }

    public void SetChannelValue(Guid clientId, int channelAddress, byte value)
    {
        lock (_lock)
        {
            ClientDmxState state = GetOrCreateState(clientId);

            state.ChannelValues[channelAddress] = value;
        }

        // Raised outside the lock so subscribers can't deadlock against other channel writes.
        OnChannelValuesChanged?.Invoke(clientId, [new DmxChannel { Address = channelAddress, Value = value }]);
    }

    public async Task<Result<IEnumerable<DmxChannel>>> GetDmxChannelsAsync(Guid clientId)
    {
        try
        {
            ClientDmxState state = GetOrCreateState(clientId);

            return Result<IEnumerable<DmxChannel>>.Ok(state.ChannelValues.Select(kvp => new DmxChannel { Address = kvp.Key, Value = kvp.Value }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX channels for client {ClientId}", clientId);

            return Result<IEnumerable<DmxChannel>>.Fail("An error occurred while retrieving DMX channels.");
        }
    }
}
