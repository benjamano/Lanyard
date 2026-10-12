using Lanyard.Infrastructure.DTO;

namespace Lanyard.Client.AudioDevices;

public interface IAudioDeviceService
{
    void Apply(ClientAudioSettingsDTO settings);

    /// <summary>
    /// Whether the preferred output device was connected at the last check; null when no
    /// preference is set or it hasn't been checked yet.
    /// </summary>
    bool? PreferredDevicePresent { get; }
}
