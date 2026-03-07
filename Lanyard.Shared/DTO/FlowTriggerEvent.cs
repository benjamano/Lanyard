namespace Lanyard.Shared.DTO;

public class FlowTriggerEvent
{
    public required string EventKey { get; set; }
    public Guid ClientId { get; set; }
    public DateTime OccurredUtc { get; set; }
    public Dictionary<string, string> Payload { get; set; } = new();
}
