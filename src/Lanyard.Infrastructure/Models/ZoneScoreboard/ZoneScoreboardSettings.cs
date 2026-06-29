using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Infrastructure.DTO.ZoneScoreboard;

public class ZoneScoreboardSettings
{
    public int Id { get; set; }

    public required Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public required string PreferredDeviceMacAddress { get; set; }

    public ZoneScoreboardVersion ZoneScoreboardVersion { get; set; }
    
    // OPTIONAL: THIS IS THE IP ADDRESS THAT THE PACKETS ARE BEING SENT TO
    public string? DestinationIp { get; set; }

    // REQUIRED: THE IP ADDRESS OF THE SOURCE DEVICE THAT IS SENDING THE PACKETS
    public required string SourceIp { get; set; }

    public bool IsActive { get; set; }
}