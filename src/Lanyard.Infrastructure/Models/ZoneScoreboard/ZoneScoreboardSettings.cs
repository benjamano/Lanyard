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

    // Has Npcap drop everything but the game's UDP traffic before the kiosk sees it. Off until a
    // venue has been checked with it on.
    public bool UseCaptureFilter { get; set; }

    // Only needed when the control box sends to some other machine. Broadcasts reach the kiosk anyway.
    public bool PromiscuousCapture { get; set; } = true;

    public bool IsActive { get; set; }
}