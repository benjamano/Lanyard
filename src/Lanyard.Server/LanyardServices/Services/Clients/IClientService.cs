using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.DTO.VideoDevices;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Application.Services;

public interface IClientService
{
    Task<Result<Client?>> GetClientFromIdAsync(Guid clientId);
    Task<Result<string?>> GetClientCurrentConnectionIdAsync(Guid clientId);
    Task<Result<Client?>> CreateClientAsync(Client newClient);
    Task<Result<Client?>> UpdateClientAsync(Client updatedClient);

    // Stamps LastDisconnectDate, and nothing else, but only while connectionId is still the
    // client's current connection. Ok(false) when it has already reconnected on a newer one. Not
    // UpdateClientAsync: that writes back the whole row, so a reconnect landing between its read
    // and its write would be undone.
    Task<Result<bool>> RecordDisconnectAsync(Guid clientId, string connectionId, DateTime disconnectedAtUtc);
    Task<Result<IEnumerable<Client>>> GetConnectedClientsAsync();
    Task<Result<IEnumerable<ClientConnectedDTO>>> GetClientsAsync();
    Task<Result<IEnumerable<ClientConnectedWithCapabilitiesDTO>>> GetClientsWithCapabilitiesAsync();
    Task<Result<IEnumerable<ClientProjectionSettings>>> GetClientProjectionSettingsAsync(Guid clientId);
    Task<Result<Guid>> GetClientIdFromConnectionIdAsync(string connectionId);
    Task<Result<bool>> SetClientAvailableScreensAsync(Guid ClientId, IEnumerable<ClientAvailableScreenDTO> screens);
    Task<Result<IEnumerable<ClientAvailableScreen>>> GetClientAvailableScreensAsync(Guid clientId);
    Task<Result<Guid>> AddClientProjectionAsync(ClientProjectionSettings clientProjectionSettings);
    Task<Result<bool>> DeleteClientProjectionSettingsAsync(Guid clientProjectionSettingsId);
    Result<IEnumerable<ClientProjectionSettingsDTO>> ConvertIntoClientProjectionSettingsDTO(IEnumerable<ClientProjectionSettings> settings);
    Task<Result<bool>> SendUpdatedProjectionProgramInfoToClientsAsync(Guid projectionProgramId);

    // Saves a kiosk's offline fallback settings (validated against KioskOfflineLimits) and re-sends
    // its programs so it refetches its offline manifest straight away.
    Task<Result<bool>> UpdateOfflineFallbackSettingsAsync(Guid clientId, bool enabled, int offlineAfterSeconds, int cacheLimitMb);

    // Puts a kiosk in one of its company's active locations (or none, with null) so it follows
    // that location's music schedule. UpdateClientAsync never writes the location.
    Task<Result<bool>> SetClientLocationAsync(Guid clientId, int? locationId);
    Task<Result<bool>> TriggerProjectionProgramOnClientAsync(Guid clientId, Guid projectionProgramId, int? displayIndex = null);
    Task<Result<bool>> CloseTemporaryProjectionWindowOnClientAsync(Guid clientId, int displayIndex);
    Task SetClientAvailableDmxDevicesAsync(Guid clientId, IEnumerable<string> dmxDevices);
    Task<Result<bool>> SetClientAvailableVideoDevicesAsync(Guid clientId, IEnumerable<ClientAvailableVideoDeviceDTO> devices);
    Task<Result<IEnumerable<ClientAvailableVideoDevice>>> GetClientAvailableVideoDevicesAsync(Guid clientId);
    Task<Result<bool>> SetClientAvailableAudioDevicesAsync(Guid clientId, IEnumerable<ClientAvailableAudioDeviceDTO> devices);
    Task<Result<IEnumerable<ClientAvailableAudioDevice>>> GetClientAvailableAudioDevicesAsync(Guid clientId);
    Task<Result<bool>> SetClientPreferredAudioDeviceAsync(Guid clientId, string? deviceId, string? deviceName);
    Task<Result<IEnumerable<string>>> GetAllActiveVideoDeviceNamesAsync();
    Task<Result<IEnumerable<ActiveVideoDeviceInfoDTO>>> GetAllActiveVideoDevicesWithClientNamesAsync();
    Task<Result<bool>> StartVideoPublisherOnClientAsync(Guid clientId, string publisherToken);
    Task<Result<bool>> StopVideoPublisherOnClientAsync(Guid clientId);
    Task<Result<bool>> RestartClientAsync(Guid clientId, ClientRestartType restartType);

    // Asks a connected kiosk to check for an update now. Ok(true) means it found one and is
    // downloading it (it restarts itself once done); Ok(false) means it's already on its target.
    Task<Result<bool>> RequestUpdateCheckAsync(Guid clientId);
    Task<Result<IEnumerable<ClientAvailableDmxDevice>>> GetClientAvailableDmxDevicesAsync(Guid clientId);
    Task<Result<bool>> SetClientPrimaryDmxDeviceAsync(Guid clientId, Guid deviceId);
    Task<Result<bool>> RemoveClientPrimaryDevice(Guid clientId, Guid deviceId);
    Task<Result<bool>> IsClientConnectedAsync(Guid clientId);
    Task<Result<ClientDmxSettingsDTO?>> GetClientDmxSettings(Guid clientId);
}
