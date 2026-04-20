using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using Lanyard.Client.SignalR;
using System.Net.Http;
using System.Windows.Forms;

public class SignalRClient(ILogger<ISignalRClient> logger) : ISignalRClient
{
    private HubConnection? _connection;
    private readonly ILogger<ISignalRClient> _logger = logger;

    private bool _isConnected = false;

    public async Task Connect(List<Action<HubConnection>> registrations)
    {
        string serverUrl = Environment.GetEnvironmentVariable("LANYARD_SERVER_URL")! + "/websocket";
        string clientId = Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!;

        _logger.LogInformation("Waiting 5 seconds to start the SignalR connection.");

        _logger.LogInformation("Connecting to SignalR server at {ServerUrl} with client ID {ClientId}", serverUrl, clientId);

        await Task.Delay(5000);
        
        while (_isConnected == false)
        {
            try
            {
                _connection = new HubConnectionBuilder()
                    .WithUrl(serverUrl + $"?clientId={clientId}")
                    .WithAutomaticReconnect(new RetryForeverPolicy())
                    .Build();

                foreach (Action<HubConnection> register in registrations)
                {
                    register(_connection);
                }

                await _connection!.StartAsync();

                _isConnected = true;

                await SendAvailableScreensToServer();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError("Error initializing SignalR connection: {Message}", ex.Message);
                _logger.LogInformation("Retrying in 5 seconds...");
                Thread.Sleep(5000);
            }
        }
    }

    private async Task SendAvailableScreensToServer()
    {
        _logger.LogInformation("Sending available screens to server...");

        IEnumerable<ClientAvailableScreenDTO> screens = Screen.AllScreens
            .Select(x=> new ClientAvailableScreenDTO()
            {
                ClientId = Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!),
                Name = x.DeviceName,
                Width = x.Bounds.Width,
                Height = x.Bounds.Height,
                Index = Array.IndexOf(Screen.AllScreens, x)
            });

        await _connection!.InvokeAsync("UpdateAvailableScreens", screens);
    }

    private async Task SendAvailableAudioDevicesToServer()
    {
        _logger.LogInformation("Sending available audio devices to server...");

        MMDeviceEnumerator enumerator = new();

        IEnumerable<ClientAvailableAudioDeviceDTO> devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(x => new ClientAvailableAudioDeviceDTO()
            {
                ClientId = Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!),
                Name = x.FriendlyName,
                Id = x.ID,
            });

        await _connection!.InvokeAsync("UpdateAvailableAudioDevices", devices);
    }

    public async Task SendLaserGameStatusAsync(LaserGameStatusDTO status)
    {
        await _connection!.InvokeAsync("UpdateLaserGameStatus", status);
    }
}

public class RetryForeverPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) => TimeSpan.FromSeconds(5);
}