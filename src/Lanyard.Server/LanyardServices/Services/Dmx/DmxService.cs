using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class DmxService(IDbContextFactory<ApplicationDbContext> factory, 
    IHubContext<SignalRControlHub> hubContext, 
    ILogger<DmxService> logger, 
    IMemoryCache cache,
    IServiceScopeFactory scopeFactory) : IDmxService, IDmxClientService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IHubContext<SignalRControlHub> _hubContext = hubContext;
    private readonly ILogger<DmxService> _logger = logger;
    private readonly IMemoryCache _cache = cache;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    private readonly Dictionary<Guid, ClientDmxState> _stateByClientId = [];
    private readonly object _lock = new();

    public event Action<Guid, int, byte>? OnChannelValueChanged;

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

    public async Task UpdateChannelValue(Guid clientId, int channelAddress, byte value)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IClientService _clientService = scope.ServiceProvider.GetRequiredService<IClientService>();

        Result<string?> clientConnectionIdGetResult = await _clientService.GetClientCurrentConnectionIdAsync(clientId);

        if (clientConnectionIdGetResult.IsSuccess && clientConnectionIdGetResult.Data != null && !string.IsNullOrEmpty(clientConnectionIdGetResult.Data))
        {
            string connectionId = clientConnectionIdGetResult.Data;
            await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveDmxChannelValue", new DmxChannel { Address = channelAddress, Value = value });
        }
    }

    public void SetChannelValue(Guid clientId, int channelAddress, byte value)
    {
        lock (_lock)
        {
            ClientDmxState state = GetOrCreateState(clientId);

            state.ChannelValues[channelAddress] = value;

            OnChannelValueChanged?.Invoke(clientId, channelAddress, value);
        }
    }

    private async Task ResetClientChannelValues(Guid clientId)
    {
        ClientDmxState state = GetOrCreateState(clientId);

        foreach (KeyValuePair<int, byte> channelValue in state.ChannelValues)
        {
            if (channelValue.Value != 0)
            {
                await UpdateChannelValue(clientId, channelValue.Key, 0);
            }
        }
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
