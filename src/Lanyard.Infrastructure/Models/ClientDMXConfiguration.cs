using Lanyard.Infrastructure.Models;

public class ClientDmxConfiguration : CreateAndUpdateBase
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public uint? DMXInterfaceDeviceId { get; set; }

    public bool IsActive { get; set; }
} 