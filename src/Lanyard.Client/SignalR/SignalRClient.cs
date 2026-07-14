using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using Lanyard.Client.SignalR;
using System.Net.Http;
using System.Net.WebSockets;
using System.Windows.Forms;
using Microsoft.AspNetCore;
using System.Net.NetworkInformation;
using Lanyard.Infrastructure.DTO.ZoneScoreboard;
using Lanyard.Client.PacketSniffing;
using DirectShowLib;
using Lanyard.Infrastructure.DTO.VideoDevices;

public class SignalRClient(ILogger<ISignalRClient> logger, DmxController dmxController, IMusicPlayer musicPlayer, IGameStateService gameStateService) : ISignalRClient
{
    private HubConnection? _connection;
    private readonly ILogger<ISignalRClient> _logger = logger;
    private readonly DmxController _dmxController = dmxController;
    private readonly IMusicPlayer _musicPlayer = musicPlayer;
    private readonly IGameStateService _gameStateService = gameStateService;

    private bool _isConnected = false;

    public async Task Connect(List<Action<HubConnection>> registrations)
    {
        string serverUrl = Environment.GetEnvironmentVariable("LANYARD_SERVER_URL")! + "/websocket";
        string clientId = Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!;
        bool allowInsecureSsl = ShouldAllowInsecureSsl();

        _logger.LogInformation("Waiting 5 seconds to start the SignalR connection.");

        _logger.LogInformation("Connecting to SignalR server at {ServerUrl} with client ID {ClientId}", serverUrl, clientId);

        if (allowInsecureSsl && serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("LANYARD_CLIENT_ALLOW_INSECURE_SSL is enabled. TLS certificate validation is disabled for the SignalR connection.");
        }

        await Task.Delay(5000);
        
        while (_isConnected == false)
        {
            try
            {
                _connection = new HubConnectionBuilder()
                    .WithUrl(serverUrl + $"?clientId={clientId}", options =>
                    {
                        if (allowInsecureSsl && serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            options.HttpMessageHandlerFactory = handler =>
                            {
                                if (handler is HttpClientHandler httpClientHandler)
                                {
                                    httpClientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                                }

                                return handler;
                            };

                            options.WebSocketConfiguration = webSocketOptions =>
                            {
                                webSocketOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                            };
                        }
                    })
                    .WithAutomaticReconnect(new RetryForeverPolicy())
                    .Build();

                _connection.Closed += async (error) =>
                {
                    int attempts = 0;
                    int maxAttempts = 5;

                    while (attempts < maxAttempts)
                    {
                        try
                        {
                            _logger.LogInformation("Attempting to reconnect to SignalR server... Attempt {Attempt} of {MaxAttempts}", attempts + 1, maxAttempts);

                            await _connection.StartAsync();

                            _isConnected = true;

                            _logger.LogInformation("Reconnected to SignalR server successfully.");

                            await SendStatus();

                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Reconnection attempt {Attempt} failed: {Message}", attempts + 1, ex.Message);
                            attempts++;
                            
                            await Task.Delay(5000);
                        }
                    }

                    _logger.LogError("Failed to reconnect to SignalR server after {MaxAttempts} attempts. Will keep trying in the background.", maxAttempts);
                };

                _connection.Reconnecting += (error) =>
                {
                    _isConnected = false;
                    _logger.LogWarning("SignalR connection lost. Attempting to reconnect...");
                    
                    return Task.CompletedTask;
                };

                _connection.Reconnected += async (connectionId) =>
                {
                    _logger.LogInformation("SignalR connection reestablished. Connection ID: {ConnectionId}", connectionId);

                    await SendStatus();

                    return;
                };

                _connection.On<string>("ReceiveMessage", (message) =>
                {
                    _logger.LogInformation("Received message from server: {Message}", message);
                });

                foreach (Action<HubConnection> register in registrations)
                {
                    register(_connection);
                }

                await _connection!.StartAsync();

                _isConnected = true;

                await SendStatus();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError("Error initializing SignalR connection: {Message}", ex.Message);

                if (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("TLS certificate validation failed. For LAN/dev testing, either set LANYARD_SERVER_URL to http://<server-ip>:5096 or set LANYARD_CLIENT_ALLOW_INSECURE_SSL=true on trusted networks.");
                }

                _logger.LogInformation("Retrying in 5 seconds...");
                Thread.Sleep(5000);
            }
        }
    }

    private static bool ShouldAllowInsecureSsl()
    {
        string? allowInsecureSsl = Environment.GetEnvironmentVariable("LANYARD_CLIENT_ALLOW_INSECURE_SSL");

        return bool.TryParse(allowInsecureSsl, out bool allow) && allow;
    }

    private async Task SendStatus()
    {
        _logger.LogInformation("Sending status to server...");

        await SendAvailableScreensToServer();
        // await SendAvailableAudioDevicesToServer();
        await SendAvailableDmxDevicesToServer();
        await SendMusicPlayerStatusToServer();
        await SendAvailableNetworkInterfacesToServer();
        await SendZoneScoreboardStatusToServer();
        await SendAvailableVideoDevicesToServer();
    }

    private async Task SendZoneScoreboardStatusToServer()
    {
        try
        {
            _logger.LogInformation("Sending zone scoreboard status to server...");

            string? clientIdValue = Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID");
            if (!Guid.TryParse(clientIdValue, out Guid clientId))
            {
                _logger.LogWarning("Cannot send laser game status: LANYARD_CLIENT_ID is missing or invalid.");
                return;
            }

            LaserGameStatusDTO status = _gameStateService.GetCurrentStatus();
            status.ClientId = clientId;

            await SendLaserGameStatusAsync(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending zone scoreboard status to server: {Message}", ex.Message);
        }
    }

    private async Task SendAvailableNetworkInterfacesToServer()
    {
        try
        {
            _logger.LogInformation("Sending available network interfaces to server...");

            IEnumerable<NetworkInterfaceDto> interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Select(x => new NetworkInterfaceDto
                {
                    PhysicalAddress = x.GetPhysicalAddress().ToString(),
                    Name = x.Name,
                })
                .DistinctBy(x => x.PhysicalAddress)
                .Where(x=> x.PhysicalAddress != PhysicalAddress.None.ToString());

            await _connection!.InvokeAsync("UpdateAvailableNetworkInterfaces", interfaces);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending available network interfaces to server: {Message}", ex.Message);
        }
    }

    private async Task SendAvailableDmxDevicesToServer()
    {
        try
        {
            _logger.LogInformation("Sending available DMX devices to server...");

            List<string> devices = _dmxController.GetAvailableDevices();

            await _connection!.InvokeAsync("UpdateAvailableDmxDevices", devices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending available DMX devices to server: {Message}", ex.Message);
        }
    }

    private async Task SendMusicPlayerStatusToServer()
    {
        _logger.LogInformation("Sending music player status to server...");

        await _musicPlayer.UpdateServerPlaybackStatus();
        await _musicPlayer.SendServerCurrentQueue();
        await _musicPlayer.SendServerCurrentVolume();
        await _musicPlayer.UpdateServerCurrentPlayingSong();
        await _musicPlayer.SendServerCurrentPlaylist();
    }

    private async Task SendAvailableScreensToServer()
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending available screens to server: {Message}", ex.Message);
        }
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

    private async Task SendAvailableVideoDevicesToServer()
    {
        try
        {
            _logger.LogInformation("Sending available video devices to server...");

            IEnumerable<ClientAvailableVideoDeviceDTO> devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)
                .Select(x => new ClientAvailableVideoDeviceDTO()
                {
                    ClientId = Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!),
                    DeviceName = x.Name,
                    DeviceId = x.ClassID,
                });

            await _connection!.InvokeAsync("UpdateAvailableVideoDevices", devices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending available video devices to server: {Message}", ex.Message);
        }
    }

    public async Task SendLaserGameStatusAsync(LaserGameStatusDTO status)
    {
        await _connection!.InvokeAsync("UpdateLaserGameStatus", status);
    }

    public async Task SendDmxChannelValueAsync(int channel, byte value)
    {
        await _connection!.InvokeAsync("UpdateDmxChannelValue", channel, value);
    }

    public async Task<string> IssueKioskTokenAsync()
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected)
        {
            return string.Empty;
        }

        return await _connection.InvokeAsync<string>("IssueKioskToken");
    }
}

public class RetryForeverPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) => TimeSpan.FromSeconds(5);
}