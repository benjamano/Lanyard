namespace Lanyard.Infrastructure.DTO.VideoDevices;

public class ActiveVideoDeviceInfoDTO
{
    public Guid ClientId { get; set; }

    public string ClientName { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;
}
