using Lanyard.Client.AudioDevices;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.Controllers;

public class AudioSettingsController(IAudioDeviceService audioDeviceService, ILogger<AudioSettingsController> logger)
{
    private readonly IAudioDeviceService _audioDeviceService = audioDeviceService;
    private readonly ILogger<AudioSettingsController> _logger = logger;

    public void Register(HubConnection connection)
    {
        connection.On<ClientAudioSettingsDTO>("ReceiveAudioSettings", settings =>
        {
            _logger.LogInformation("Received audio settings: preferred device {DeviceName} ({DeviceId})", settings.PreferredDeviceName, settings.PreferredDeviceId);

            _audioDeviceService.Apply(settings);
        });
    }
}
