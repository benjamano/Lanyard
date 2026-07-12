namespace Lanyard.Infrastructure.DTO.VideoDevices;

public class ClientAvailableVideoDeviceDTO
{
    public string DeviceName { get; set; } = string.Empty;

    public Guid DeviceId { get; set; }

    public Guid ClientId { get; set; }
}