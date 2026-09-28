using Lanyard.Infrastructure.DTO;

namespace Lanyard.Client.AudioDevices;

public interface IAudioDeviceService
{
    void Apply(ClientAudioSettingsDTO settings);
}
