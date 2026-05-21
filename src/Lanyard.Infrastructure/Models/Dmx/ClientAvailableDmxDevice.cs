namespace Lanyard.Infrastructure.Models.Dmx;

public class ClientAvailableDmxDevice
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public uint DeviceIndex { get; set; }
    public string Name { get; set; } = string.Empty;

    public bool IsPrimaryDevice { get; set; }

    public bool IsActive { get; set; }
}