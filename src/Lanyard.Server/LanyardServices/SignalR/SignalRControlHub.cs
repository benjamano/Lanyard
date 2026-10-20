using Lanyard.Application.Services;
using Lanyard.Application.Services.AudioClips;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Clients;
using Lanyard.Application.Services.VideoStreaming;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.DTO.VideoDevices;
using Lanyard.Infrastructure.DTO.ZoneScoreboard;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;

namespace Lanyard.Application.SignalR;

public class SignalRControlHub(
    ILogger<SignalRControlHub> logger,
    MusicPlayerService playerService,
    IClientService clientService,
    ILaserGameStatusStore laserGameStatusStore,
    SignalRProjectionControlHubEvents hubEvents,
    AutomationEngineService automationEngineService,
    IDmxClientService dmxClientService,
    IClientZoneScoreboardService clientZoneScoreboardService,
    IVideoStreamTokenService videoStreamTokenService,
    IGameResultService gameResultService,
    IKioskMediaTokenService mediaTokenService,
    KioskConnectionRegistry connectionRegistry,
    IKioskHealthService kioskHealthService,
    IAudioClipPlaybackService audioClipPlaybackService) : Hub, ISignalRProjectionControlHub
{
    private readonly ILogger<SignalRControlHub> _logger = logger;
    private readonly IKioskMediaTokenService _mediaTokenService = mediaTokenService;
    private readonly KioskConnectionRegistry _connectionRegistry = connectionRegistry;

    private readonly MusicPlayerService _playerService = playerService;
    private readonly IClientService _clientService = clientService;
    private readonly SignalRProjectionControlHubEvents _hubEvents = hubEvents;
    private readonly ILaserGameStatusStore _laserGameStatusStore = laserGameStatusStore;
    private readonly AutomationEngineService _automationEngineService = automationEngineService;
    private readonly IDmxClientService _dmxClientService = dmxClientService;
    private readonly IClientZoneScoreboardService _clientZoneScoreboardService = clientZoneScoreboardService;
    private readonly IVideoStreamTokenService _videoStreamTokenService = videoStreamTokenService;
    private readonly IGameResultService _gameResultService = gameResultService;
    private readonly IKioskHealthService _kioskHealthService = kioskHealthService;
    private readonly IAudioClipPlaybackService _audioClipPlaybackService = audioClipPlaybackService;

    private static readonly ConcurrentDictionary<string, bool> _connections = new();

    public static IReadOnlyCollection<string> ConnectedIds => (IReadOnlyCollection<string>)_connections.Keys;

    public override async Task OnConnectedAsync()
    {
        HttpContext? httpContext = Context.GetHttpContext();

        // The endpoint policy (KioskAuthenticationDefaults.HubPolicy) already turned away anyone
        // who isn't a kiosk the server knows, so this is the kiosk's own id, from its credential.
        // Never a new id - an unknown kiosk must be paired by an Admin, not created on connect.
        Guid? authenticatedClientId = KioskRequestAuthorization.GetClientId(Context.User);

        if (authenticatedClientId is not Guid clientId)
        {
            _logger.LogWarning("Kiosk connection {ConnectionId} has no known client id, disconnecting. A new kiosk must be paired before it can connect", Context.ConnectionId);

            Context.Abort();
            return;
        }

        bool usesCredential = Context.User?.Identity?.AuthenticationType == KioskAuthenticationDefaults.CredentialAuthenticationType;

        string? claimedClientId = httpContext?.Request.Query[ClientConnectionQuery.ClientId].ToString();

        if (usesCredential && !string.IsNullOrEmpty(claimedClientId) && (!Guid.TryParse(claimedClientId, out Guid claimed) || claimed != clientId))
        {
            // Not trusted either way - the credential decides - but worth knowing about.
            _logger.LogWarning("Kiosk {ClientId} ({ConnectionId}) sent a different client id {ClaimedClientId} from its credential's; using the credential's", clientId, Context.ConnectionId, claimedClientId);
        }

        string clientIp = httpContext?.Connection.RemoteIpAddress?.ToString() ?? "Unknown";

        bool supportsDmxBatch = httpContext?.Request.Query[ClientConnectionQuery.DmxBatch].ToString() == "1";
        _dmxClientService.SetConnectionSupportsBatch(Context.ConnectionId, supportsDmxBatch);

        if (!supportsDmxBatch)
        {
            _logger.LogInformation("Client {ClientId} ({ConnectionId}) predates batched DMX; sending it one message per channel", clientId, Context.ConnectionId);
        }

        string? rawClientVersion = httpContext?.Request.Query[ClientConnectionQuery.ClientVersion].ToString();

        Result<Client?> result = await _clientService.GetClientFromIdAsync(clientId);

        if (!result.IsSuccess || result.Data == null)
        {
            // Authenticated a moment ago, so the row has just gone (or the read failed).
            _logger.LogWarning("Kiosk {ClientId} ({ConnectionId}) authenticated but its client record couldn't be loaded, disconnecting: {Error}", clientId, Context.ConnectionId, result.Error);

            Context.Abort();
            return;
        }

        Client client = result.Data;

        client.LastUpdateDate = DateTime.UtcNow;
        client.MostRecentConnectionId = Context.ConnectionId;
        client.MostRecentIpAddress = clientIp;
        client.LastLogin = DateTime.UtcNow;
        client.LastAuthMethod = usesCredential ? ClientAuthMethod.Credential : ClientAuthMethod.LegacySecret;

        LogClientVersionReport(clientId, ClientVersionReport.Apply(client, rawClientVersion, DateTime.UtcNow), rawClientVersion);

        Result<Client?> updateResult = await _clientService.UpdateClientAsync(client);

        if (!updateResult.IsSuccess)
        {
            _logger.LogError("Failed to update client entry for ID {ClientId}: {Error}", clientId, updateResult.Error);

            Context.Abort();
            return;
        }

        _logger.LogInformation("Kiosk {ClientId} ({ConnectionId}) authenticated with {AuthMethod}", clientId, Context.ConnectionId, client.LastAuthMethod);

        // So revoking the kiosk can close this connection straight away.
        _connectionRegistry.Register(clientId, Context);

        await SendProjectionProgramInfoToClientAsync(client.Id);
        await SendMusicSettingsToClientAsync(client);
        await SendDmxSettingsToClientAsync(client);
        await SendZoneScoreboardSettingsToClientAsync(client);
        await SendRestartScheduleToClientAsync(client);
        await SendAudioSettingsToClientAsync(client);

        string musicGroup = MusicGroupFor(client.CompanyId);
        Context.Items[MusicGroupItemKey] = musicGroup;

        await Groups.AddToGroupAsync(Context.ConnectionId, musicGroup);

        _logger.LogInformation("Client {ClientName} ({ConnectionId}) connected and added to {MusicGroup}", client.Name, Context.ConnectionId, musicGroup);

        _connections.TryAdd(Context.ConnectionId, true);

        // So a scheduled clip is already on the kiosk's disk when it's due. Logs its own failures.
        await _audioClipPlaybackService.PrefetchScheduledClipsAsync(client.Id, Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    private void LogClientVersionReport(Guid clientId, ClientVersionReportOutcome outcome, string? rawClientVersion)
    {
        switch (outcome)
        {
            case ClientVersionReportOutcome.Recorded:
                _logger.LogInformation("Client {ClientId} ({ConnectionId}) reported client version {ClientVersion}", clientId, Context.ConnectionId, rawClientVersion?.Trim());
                break;
            case ClientVersionReportOutcome.NotReported:
                _logger.LogInformation("Client {ClientId} ({ConnectionId}) did not report a client version; it predates version reporting", clientId, Context.ConnectionId);
                break;
            case ClientVersionReportOutcome.Rejected:
                // Truncated: the value is untrusted and only the shape of it is useful in a log.
                string sample = rawClientVersion is { Length: > ClientVersionFormat.MaxLength }
                    ? rawClientVersion[..ClientVersionFormat.MaxLength] + "..."
                    : rawClientVersion ?? string.Empty;
                _logger.LogWarning("Client {ClientId} ({ConnectionId}) reported an invalid client version {ClientVersion}; ignoring it", clientId, Context.ConnectionId, sample);
                break;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(MusicGroupItemKey, out object? musicGroup) && musicGroup is string group)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
        }

        _logger.LogInformation("Client {ConnectionId} disconnected from its Music group", Context.ConnectionId);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (getClientResult.IsSuccess)
        {
            // Only clear the live game status while this is still the client's current
            // connection - a kiosk that has already reconnected on a newer connection before
            // this stale socket's disconnect event fires would otherwise have the newer
            // connection's just-published status wiped out by the old connection's disconnect.
            if (await IsCurrentConnectionForClientAsync(getClientResult.Data))
            {
                _laserGameStatusStore.RemoveStatus(getClientResult.Data);
            }

            await RecordClientDisconnectAsync(getClientResult.Data);
        }

        _connections.TryRemove(Context.ConnectionId, out _);
        _connectionRegistry.Unregister(Context.ConnectionId);
        _dmxClientService.ForgetConnection(Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }

    private async Task<bool> IsCurrentConnectionForClientAsync(Guid clientId)
    {
        Result<Client?> clientResult = await _clientService.GetClientFromIdAsync(clientId);

        return clientResult.IsSuccess && clientResult.Data?.MostRecentConnectionId == Context.ConnectionId;
    }

    // Stamps the client row so a dashboard can show how long a kiosk has been down. Only
    // stamps when the row still points at this connection: a kiosk that reconnects before its
    // old socket's disconnect event fires has already written a newer connection ID, and
    // overwriting here would report a live client as offline. The check and the write happen
    // together in RecordDisconnectAsync, which touches no other column.
    private async Task RecordClientDisconnectAsync(Guid clientId)
    {
        Result<bool> result = await _clientService.RecordDisconnectAsync(clientId, Context.ConnectionId, DateTime.UtcNow);

        if (!result.IsSuccess)
        {
            _logger.LogWarning("Failed to record disconnect timestamp for client {ClientId}: {Error}", clientId, result.Error);

            return;
        }

        if (!result.Data)
        {
            _logger.LogInformation("Skipping disconnect timestamp for client {ClientId}: it has already reconnected on a newer connection", clientId);

            return;
        }

        _logger.LogInformation("Recorded disconnect for client {ClientId} ({ConnectionId})", clientId, Context.ConnectionId);
    }

    public async Task PlaybackStateChanged(PlaybackState state)
    {
        _logger.LogInformation("Client {ConnectionId} reported playback state: {State}", Context.ConnectionId, state);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        _playerService.UpdatePlaybackState(getClientResult.Data, state);
    }

    public async Task PlaybackPositionChanged(double positionSeconds)
    {
        // Fires every second while a client is playing - deliberately not logged
        // per report to keep the server log readable.
        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        _playerService.UpdatePlaybackPosition(getClientResult.Data, positionSeconds);
    }

    public async Task SongEnded()
    {
        _logger.LogInformation("Client {ConnectionId} reported its track finished", Context.ConnectionId);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        await _playerService.HandleSongEndedAsync(getClientResult.Data);
    }

    public async Task CurrentPlayingSongChanged(Guid song)
    {
        _logger.LogInformation("Client {ConnectionId} reported new song: {Song}", Context.ConnectionId, song);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        _playerService.UpdateCurrentSong(getClientResult.Data, song);
    }

    public async Task UpdateAvailableScreens(IEnumerable<ClientAvailableScreenDTO> screens)
    {
        _logger.LogInformation("Client {ConnectionId} reported available screens: {Screens}", Context.ConnectionId, screens.Select(x => x.Name));

        Result<Guid> getResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getResult.IsSuccess)
        {
            _logger.LogError("Failed to get client ID from connection ID {ConnectionId}: {Error}", Context.ConnectionId, getResult.Error);
            return;
        }

        Guid clientId = getResult.Data!;

        await _clientService.SetClientAvailableScreensAsync(clientId, screens);
    }

    public async Task UpdateLaserGameStatus(LaserGameStatusDTO status)
    {
        Result<Guid> getResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID for laser status update from {ConnectionId}: {Error}", Context.ConnectionId, getResult.Error);
            return;
        }

        Guid clientId = getResult.Data;

        // Read the previous status before overwriting it, so the InGame -> NotStarted edge can be
        // detected here. This has to be strictly edge-triggered: the kiosk goes on republishing
        // the finished game's scores for the whole idle period (GameStateService only clears them
        // on the next game start), so recording on any NotStarted heartbeat would write the same
        // game over and over.
        _laserGameStatusStore.TryGetStatus(clientId, out LaserGameStatusDTO? previousStatus);

        bool gameJustEnded = previousStatus?.Status == GameStatus.InGame && status.Status == GameStatus.NotStarted;

        _laserGameStatusStore.UpdateStatus(clientId, status);
        _automationEngineService.EnqueueTransition(clientId, status.Status, status);

        // After EnqueueTransition, so a publish that starts a game is counted as that new game.
        _automationEngineService.EnqueueGameData(clientId, status);

        if (gameJustEnded)
        {
            Result<bool> recordResult = await _gameResultService.RecordCompletedGameAsync(clientId, status);

            if (!recordResult.IsSuccess)
            {
                _logger.LogWarning("Failed to record completed game for client {ClientId}: {Error}", clientId, recordResult.Error);
            }
        }
    }

    // Called by the kiosk's KioskHealthReporter every heartbeat; the name must match its
    // InvokeAsync call. Returns whether the report was stored, so the client knows it landed.
    public async Task<bool> ReportKioskHealth(KioskHealthReportDTO report)
    {
        Result<Guid> getResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID for health report from {ConnectionId}: {Error}", Context.ConnectionId, getResult.Error);
            return false;
        }

        Result<bool> recordResult = await _kioskHealthService.RecordReportAsync(getResult.Data, report);

        if (!recordResult.IsSuccess)
        {
            _logger.LogWarning("Failed to record health report for client {ClientId}: {Error}", getResult.Data, recordResult.Error);
            return false;
        }

        return recordResult.Data;
    }

    // Kiosks share one hub, so music commands go only to kiosks of the sender's own company.
    private const string MusicGroupItemKey = "MusicGroup";

    private static string MusicGroupFor(int companyId) => $"{ClientGroup.Music}:{companyId}";

    private IClientProxy MusicGroupClients() =>
        Context.Items.TryGetValue(MusicGroupItemKey, out object? group) && group is string name
            ? Clients.Group(name)
            : Clients.Clients([]);

    public async Task Load(Guid songId)
    {
        _logger.LogInformation("Load command received for song {SongId}", songId);

        await MusicGroupClients().SendAsync("Load", songId);
    }

    public async Task Play()
    {
        _logger.LogInformation("Play command received");

        await MusicGroupClients().SendAsync("Play");
    }

    public async Task Pause()
    {
        _logger.LogInformation("Pause command received");

        await MusicGroupClients().SendAsync("Pause");
    }

    public async Task Stop()
    {
        _logger.LogInformation("Stop command received");

        await MusicGroupClients().SendAsync("Stop");
    }

    private async Task SendMusicSettingsToClientAsync(Client client)
    {
        ClientMusicSettingsDTO settings = new ClientMusicSettingsDTO { CacheLimitMb = client.MusicCacheLimitMb };

        await Clients.Caller.SendAsync("ReceiveMusicSettings", settings);
        _logger.LogInformation("Sent music settings to client {ClientId}: cache limit {CacheLimitMb}MB", client.Id, client.MusicCacheLimitMb);
    }

    private async Task SendRestartScheduleToClientAsync(Client client)
    {
        ClientRestartScheduleDTO schedule = new ClientRestartScheduleDTO
        {
            Enabled = client.AutoRestartEnabled,
            IntervalUnit = client.AutoRestartIntervalUnit,
            IntervalCount = client.AutoRestartIntervalCount,
            TimeOfDay = client.AutoRestartTimeOfDay
        };

        await Clients.Caller.SendAsync("ReceiveRestartSchedule", schedule);
        _logger.LogInformation("Sent restart schedule to client {ClientId}: enabled {Enabled}, every {IntervalCount} {IntervalUnit} at {TimeOfDay}", client.Id, client.AutoRestartEnabled, client.AutoRestartIntervalCount, client.AutoRestartIntervalUnit, client.AutoRestartTimeOfDay);
    }

    private async Task SendAudioSettingsToClientAsync(Client client)
    {
        ClientAudioSettingsDTO settings = new ClientAudioSettingsDTO
        {
            PreferredDeviceId = client.PreferredAudioDeviceId,
            PreferredDeviceName = client.PreferredAudioDeviceName
        };

        await Clients.Caller.SendAsync("ReceiveAudioSettings", settings);
        _logger.LogInformation("Sent audio settings to client {ClientId}: preferred device {DeviceName} ({DeviceId})", client.Id, client.PreferredAudioDeviceName, client.PreferredAudioDeviceId);
    }

    private async Task SendZoneScoreboardSettingsToClientAsync(Client client)
    {
        Result<ZoneScoreboardSettings?> getResult = await _clientZoneScoreboardService.GetZoneScoreboardSettingsAsync(client.Id);

        if (!getResult.IsSuccess || getResult.Data == null)
        {
            _logger.LogError("Failed to get zone scoreboard settings for client {ClientId}: {Error}", client.Id, getResult.Error);
            return;
        }

        ZoneScoreboardSettings settings = getResult.Data;

        ZoneScoreboardSettingsDTO settingsDto = new()
        {
            PreferredDeviceMacAddress = settings.PreferredDeviceMacAddress,
            ZoneScoreboardVersion = settings.ZoneScoreboardVersion,
            SourceIp = settings.SourceIp,
            DestinationIp = settings.DestinationIp
        };

        await Clients.Caller.SendAsync("ReceiveZoneScoreboardSettings", settingsDto);
        _logger.LogInformation("Sent zone scoreboard settings to client {ClientId}", client.Id);
    }

    public async Task<Result<bool>> SendProjectionProgramInfoToClientAsync(Guid clientId)
    {
        try
        {
            Result<Client?> getResult = await _clientService.GetClientFromIdAsync(clientId);

            if (!getResult.IsSuccess || getResult.Data == null)
            {
                _logger.LogError("Failed to get client {ClientId}: {Error}", clientId, getResult.Error);
                return Result<bool>.Fail("Failed to get client.");
            }

            string? connectionId = getResult.Data!.MostRecentConnectionId;

            if (string.IsNullOrEmpty(connectionId))
            {
                _logger.LogWarning("Client {ClientId} has no recent connection ID", clientId);
                return Result<bool>.Fail("Client has no recent connection ID.");
            }

            Result<IEnumerable<ClientProjectionSettings>> result = await _clientService.GetClientProjectionSettingsAsync(clientId);

            if (!result.IsSuccess)
            {
                _logger.LogError("Failed to get projection settings for client {ConnectionId}: {Error}", Context.ConnectionId, result.Error);
                return Result<bool>.Fail("Failed to get projection settings.");
            }

            Result<IEnumerable<ClientProjectionSettingsDTO>> result1 = _clientService.ConvertIntoClientProjectionSettingsDTO(result.Data!);

            if (!result1.IsSuccess)
            {
                _logger.LogError("Failed to convert projection settings into DTO for client {ConnectionId}: {Error}", Context.ConnectionId, result.Error);
                return Result<bool>.Fail("Failed to convert projection settings into DTO.");
            }

            if (result1.Data == null || !result1.Data.Any())
            {
                return Result<bool>.Ok(true);
            }

            _logger.LogInformation("Sending projection program info to client {ConnectionId}", connectionId);

            await Clients.Clients(connectionId).SendAsync("ReceiveProjectionPrograms", result1.Data);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while sending projection program info to client {clientId}", clientId);

            return Result<bool>.Fail("An error occurred while sending projection program info to client.");
        }
    }
    public async Task ProjectionProgramCompleted()
    {
        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);

        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("ProjectionProgramCompleted: failed to resolve client ID for connection {ConnectionId}", Context.ConnectionId);
            return;
        }

        _logger.LogInformation("Client {ClientId} completed triggered projection program, restoring original settings", getClientResult.Data);

        await SendProjectionProgramInfoToClientAsync(getClientResult.Data);
    }

    public async Task ReceiveCachedSongs(Result<IEnumerable<CachedSongDTO>> cachedSongs)
    {
        await _hubEvents.RaiseReceiveCachedSongs(cachedSongs);
    }

    public async Task UpdateAvailableDmxDevices(IEnumerable<string> devices)
    {
        _logger.LogInformation("Client {ConnectionId} reported available DMX devices: {Devices}", Context.ConnectionId, devices);

        Result<Guid> getResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getResult.IsSuccess)
        {
            _logger.LogError("Failed to get client ID from connection ID {ConnectionId}: {Error}", Context.ConnectionId, getResult.Error);
            return;
        }

        Guid clientId = getResult.Data!;

        await _clientService.SetClientAvailableDmxDevicesAsync(clientId, devices);
    }

    public async Task PlayerVolumeChanged(int volume)
    {
        _logger.LogInformation("Client {ConnectionId} reported volume change: {Volume}", Context.ConnectionId, volume);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        _playerService.ReportVolumeFromClient(getClientResult.Data, volume);
    }

    // Hub methods are called by their C# name, so this must stay equal to
    // ClientHubMethods.AudioClipPlaybackChanged (a test pins it).
    public async Task AudioClipPlaybackChanged(AudioClipPlaybackReport report)
    {
        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        Result<bool> recorded = await _audioClipPlaybackService.ReportFromClientAsync(getClientResult.Data, report);
        if (!recorded.IsSuccess)
        {
            _logger.LogWarning("Failed to record clip play {PlayId} from client {ClientId}: {Error}", report.PlayId, getClientResult.Data, recorded.Error);
        }
    }

    public async Task PlaylistChanged(Guid playlistId)
    {
        _logger.LogInformation("Client {ConnectionId} reported playlist change: {PlaylistId}", Context.ConnectionId, playlistId);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        await _playerService.SetCurrentPlaylist(getClientResult.Data, playlistId);
    }

    public async Task QueueChanged(List<Guid> queue)
    {
        _logger.LogDebug("Client {ConnectionId} reported queue change: {Queue}", Context.ConnectionId, queue);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        Guid playlistId = _playerService.GetCurrentPlaylist(getClientResult.Data)?.Id ?? Guid.Empty;

        await _playerService.SetQueue(getClientResult.Data, queue, playlistId);
    }

    public async Task UpdateDmxChannelValue(int channelAddress, byte value)
    {
        _logger.LogDebug("Client {ConnectionId} reported DMX channel update: Address {ChannelAddress}, Value {Value}", Context.ConnectionId, channelAddress, value);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        Guid clientId = getClientResult.Data;

        _dmxClientService.SetChannelValue(clientId, channelAddress, value);
    }

    public async Task SendDmxSettingsToClientAsync(Client client)
    {
        Result<ClientDmxSettingsDTO?> settingsResult = await _clientService.GetClientDmxSettings(client.Id);

        if (!settingsResult.IsSuccess)
        {
            _logger.LogError("Failed to get DMX settings for client {ClientId}: {Error}", client.Id, settingsResult.Error);
            return;
        }

        if (settingsResult.Data == null)
        {
            _logger.LogWarning("No DMX settings found for client {ClientId}", client.Id);
            return;
        }

        await Clients.Caller.SendAsync("ReceiveDmxSettings", settingsResult.Data);
    }

    public async Task UpdateAvailableNetworkInterfaces(IEnumerable<NetworkInterfaceDto> interfaces)
    {
        _logger.LogInformation("Client {ConnectionId} reported available network interfaces: {Interfaces}", Context.ConnectionId, interfaces);

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);
        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        Guid clientId = getClientResult.Data;

        IEnumerable<NetworkInterfaceDto> physicalAddresses = interfaces.Select(i => new NetworkInterfaceDto
        {
            PhysicalAddress = i.PhysicalAddress,
            Name = i.Name
        });

        await _clientZoneScoreboardService.UpdateClientAvailableNetworkInterfacesAsync(clientId, physicalAddresses);
    }

    public async Task UpdateAvailableVideoDevices(IEnumerable<ClientAvailableVideoDeviceDTO> devices)
    {
        _logger.LogInformation("Client {ConnectionId} reported available video devices: {Devices}", Context.ConnectionId, devices.Select(d => d.DeviceName));

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);

        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        Guid clientId = getClientResult.Data;

        Result<bool> setResult = await _clientService.SetClientAvailableVideoDevicesAsync(clientId, devices);

        if (!setResult.IsSuccess)
        {
            _logger.LogWarning("Failed to update available video devices for client {ClientId}: {Error}", clientId, setResult.Error);
        }
    }

    public async Task UpdateAvailableAudioDevices(IEnumerable<ClientAvailableAudioDeviceDTO> devices)
    {
        _logger.LogInformation("Client {ConnectionId} reported available audio devices: {Devices}", Context.ConnectionId, devices.Select(d => d.Name));

        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);

        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to resolve client ID from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return;
        }

        Guid clientId = getClientResult.Data;

        Result<bool> setResult = await _clientService.SetClientAvailableAudioDevicesAsync(clientId, devices);

        if (!setResult.IsSuccess)
        {
            _logger.LogWarning("Failed to update available audio devices for client {ClientId}: {Error}", clientId, setResult.Error);
        }
    }

    /// <summary>
    /// Issues a capability token that authorises this client's kiosk to open remote video
    /// streams. Called by the client just before it launches a kiosk window; the token is
    /// bound to the client resolved from this hub connection, so it cannot be minted by
    /// merely opening the kiosk URL.
    /// </summary>
    public async Task<string> IssueKioskToken()
    {
        Result<Guid> getClientResult = await _clientService.GetClientIdFromConnectionIdAsync(Context.ConnectionId);

        if (!getClientResult.IsSuccess)
        {
            _logger.LogWarning("Failed to issue kiosk token; could not resolve client from connection {ConnectionId}: {Error}", Context.ConnectionId, getClientResult.Error);
            return string.Empty;
        }

        return _videoStreamTokenService.IssueViewerToken(getClientResult.Data);
    }

    // ClientHubMethods.IssueMediaToken. Only for a kiosk with its own credential: the token stands
    // in for that credential, and dies with it.
    public KioskMediaTokenDTO? IssueMediaToken()
    {
        Guid? clientId = KioskRequestAuthorization.GetClientId(Context.User);
        Guid? credentialId = KioskRequestAuthorization.GetCredentialId(Context.User);

        if (clientId is not Guid kioskId || credentialId is not Guid kioskCredentialId
            || !int.TryParse(Context.User?.FindFirst(LocationClaimTypes.CompanyId)?.Value, out int companyId))
        {
            _logger.LogWarning("Connection {ConnectionId} asked for a media token without a kiosk credential", Context.ConnectionId);
            return null;
        }

        string token = _mediaTokenService.Issue(new KioskIdentity(kioskId, companyId, kioskCredentialId));

        return new KioskMediaTokenDTO(token, DateTime.UtcNow.Add(_mediaTokenService.Lifetime));
    }
}
