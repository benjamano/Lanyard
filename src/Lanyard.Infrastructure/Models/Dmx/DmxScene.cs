namespace Lanyard.Infrastructure.Models.Dmx;

public class DmxScene : CreateAndUpdateBase
{    
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public bool Loop { get; set; } = true;

    public bool IsMomentary { get; set; }

    public virtual ICollection<DmxSceneStep> Steps { get; set; } = [];
}