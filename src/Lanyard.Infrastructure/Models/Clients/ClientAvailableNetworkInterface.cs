using System.Net.NetworkInformation;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO.ZoneScoreboard;

public class ClientAvailableNetworkInterface
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public required PhysicalAddress MacAddress { get; set; }

    public DateTime LastSeenDate { get; set; }

    public bool IsActive { get; set; }
}