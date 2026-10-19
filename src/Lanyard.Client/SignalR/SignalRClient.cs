using Lanyard.Client.Enrolment;
using Lanyard.Client.Security;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.SignalR;
using Lanyard.Shared.DTO;
using AutoUpdater = Lanyard.Client.AutoUpdate.AutoUpdate;
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

        // The kiosk's own credential goes in the Authorization header (AccessTokenProvider, in
        // BuildConnection), never the URL. Only a kiosk still on the old shared secret puts that
        // in the query string, as every kiosk did before 1.0.43.
        string? legacySecret = KioskCredentials.Current is null ? KioskCredentials.LegacySecret : null;

        string secretQuery = legacySecret is null
            ? string.Empty
            : $"&{ClientConnectionQuery.Secret}={Uri.EscapeDataString(legacySecret)}";

        // Lets the server show which build each kiosk runs. Left off entirely for a dev build
        // that isn't a Velopack install, so the server keeps "unknown" rather than a fake value.
        string? clientVersion = AutoUpdater.GetCurrentVersion();
        string versionQuery = string.IsNullOrWhiteSpace(clientVersion)
            ? string.Empty
            : $"&{ClientConnectionQuery.ClientVersion}={Uri.EscapeDataString(clientVersion)}";

        // dmxBatch=1 tells the server this client handles "ReceiveDmxChannelValues". Clients
        // older than 1.0.40 don't send it, and the server falls back to one message per channel.
        string url = serverUrl + $"?{ClientConnectionQuery.ClientId}={clientId}{secretQuery}&{ClientConnectionQuery.DmxBatch}=1{versionQuery}";

        _logger.LogInformation("Waiting 5 seconds to start the SignalR connection.");

        _logger.LogInformation("Connecting to SignalR server at {ServerUrl} with client ID {ClientId}", serverUrl, clientId);

        if (allowInsecureSsl && serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("LANYARD_CLIENT_ALLOW_INSECURE_SSL is enabled. TLS certificate validation is disabled for the SignalR connection.");
        }

        await Task.Delay(ConnectRetryDelay.Initial);

        // Never throws: a kiosk that can't reach the server must keep running (and keep trying),
        // not fall through to App's fatal-startup Environment.Exit. Previously only
        // HttpRequestException was caught, so a negotiate timeout or WebSocket fault killed the process.
        int failedAttempts = 0;

        while (_isConnected == false)
        {
            bool unauthorized = false;

            try
            {
                HubConnection? previous = _connection;

                _connection = BuildConnection(url, allowInsecureSsl, registrations);

                if (previous != null)
                {
                    // A prior failed attempt built a HubConnection that's being replaced -
                    // dispose it so it isn't leaked during connect/retry storms.
                    await previous.DisposeAsync();
                }

                await _connection.StartAsync();

                SetConnected(true);

                await SendStatus();
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                unauthorized = true;

                await HandleRejectedAsync(clientId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error initializing SignalR connection: {ExceptionType}: {Message}", ex.GetType().Name, ex.Message);

                if (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("TLS certificate validation failed. For LAN/dev testing, either set LANYARD_SERVER_URL to http://<server-ip>:5096 or set LANYARD_CLIENT_ALLOW_INSECURE_SSL=true on trusted networks.");
                }
            }

            if (_isConnected)
            {
                break;
            }

            failedAttempts++;
            TimeSpan delay = ConnectRetryDelay.For(failedAttempts, unauthorized);

            _logger.LogInformation("Retrying SignalR connection in {DelaySeconds} seconds (attempt {Attempt})", delay.TotalSeconds, failedAttempts);
            await Task.Delay(delay);
        }
    }

    public event Action<bool>? ConnectionStateChanged;

    // The server turned the connection away. If it says this kiosk's credential is dead (revoked
    // by an Admin, or replaced), forget it and restart: startup then shows a pairing code. Anything
    // less certain just retries slowly, so a server hiccup never costs a kiosk its credential.
    private async Task HandleRejectedAsync(string clientId)
    {
        if (Guid.TryParse(clientId, out Guid id) && await KioskEnrolmentStartup.IsCredentialRejectedAsync(id))
        {
            _logger.LogError("The server no longer accepts this kiosk's credential (revoked or replaced). Restarting to pair it again.");

            KioskCredentials.Forget();

            // The Watchdog relaunches the client straight away on this exit code.
            Environment.Exit(RestartRequestedExitCode);
        }

        _logger.LogError(KioskCredentials.Current is null
            ? "SignalR connection refused: the server didn't accept this kiosk's shared secret. Restart the kiosk to pair it."
            : "SignalR connection refused, but the server didn't say the credential is invalid. Retrying.");
    }

    private const int RestartRequestedExitCode = 75;

    private void SetConnected(bool connected)
    {
        if (_isConnected == connected)
        {
            return;
        }

        _isConnected = connected;

        try
        {
            ConnectionStateChanged?.Invoke(connected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A ConnectionStateChanged handler threw");
        }
    }

    private HubConnection BuildConnection(string url, bool allowInsecureSsl, List<Action<HubConnection>> registrations)
    {
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                // Asked again on every connect and reconnect, so a rotated credential is picked up.
                options.AccessTokenProvider = () => Task.FromResult(KioskCredentials.Current);

                if (allowInsecureSsl && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
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

        connection.Closed += error => HandleClosedAsync(error, url, allowInsecureSsl, registrations);

        connection.Reconnecting += (error) =>
        {
            SetConnected(false);
            _logger.LogWarning("SignalR connection lost. Attempting to reconnect...");

            return Task.CompletedTask;
        };

        connection.Reconnected += async (connectionId) =>
        {
            _logger.LogInformation("SignalR connection reestablished. Connection ID: {ConnectionId}", connectionId);

            SetConnected(true);

            await SendStatus();
        };

        foreach (Action<HubConnection> register in registrations)
        {
            register(connection);
        }

        KioskMediaTokens.Issuer = () => connection.InvokeAsync<KioskMediaTokenDTO?>(ClientHubMethods.IssueMediaToken);

        return connection;
    }

    /// <summary>
    /// WithAutomaticReconnect's RetryForeverPolicy never gives up on its own, so Closed only fires
    /// for faults automatic reconnect can't recover from itself (e.g. a transport-level break during
    /// the handshake, like an HTTP/2 CANCEL stream reset). Rebuilds the connection from scratch and
    /// retries indefinitely instead of giving up after a handful of attempts, which previously left
    /// the client permanently disconnected despite logging that it would "keep trying in the background".
    /// </summary>
    private async Task HandleClosedAsync(Exception? error, string url, bool allowInsecureSsl, List<Action<HubConnection>> registrations)
    {
        SetConnected(false);
        _logger.LogWarning(error, "SignalR connection closed unexpectedly outside automatic reconnect. Rebuilding connection.");

        while (_isConnected == false)
        {
            try
            {
                HubConnection? previous = _connection;

                _connection = BuildConnection(url, allowInsecureSsl, registrations);

                if (previous != null)
                {
                    await previous.DisposeAsync();
                }

                await _connection.StartAsync();

                SetConnected(true);

                _logger.LogInformation("Reconnected to SignalR server successfully after an unexpected close.");

                await SendStatus();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnect-after-close attempt failed, retrying in 5 seconds.");

                await Task.Delay(5000);
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
        await SendAvailableAudioDevicesToServer();
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
        try
        {
            _logger.LogInformation("Sending available audio devices to server...");

            using MMDeviceEnumerator enumerator = new();

            List<ClientAvailableAudioDeviceDTO> devices = [];

            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new ClientAvailableAudioDeviceDTO()
                    {
                        ClientId = Guid.Parse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ID")!),
                        Name = device.FriendlyName,
                        Id = device.ID,
                    });
                }
            }

            await _connection!.InvokeAsync("UpdateAvailableAudioDevices", devices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending available audio devices to server: {Message}", ex.Message);
        }
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

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task<bool> ReportKioskHealthAsync(KioskHealthReportDTO report)
    {
        if (_connection is null || _connection.State != HubConnectionState.Connected)
        {
            return false;
        }

        return await _connection.InvokeAsync<bool>("ReportKioskHealth", report);
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