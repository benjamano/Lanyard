namespace Lanyard.Infrastructure.DTO.VideoDevices;

public class ClientAvailableVideoDeviceDTO
{
    public string Name { get; set; } = string.Empty;

    public Guid DeviceId { get; set; }

    public Guid ClientId { get; set; }
}