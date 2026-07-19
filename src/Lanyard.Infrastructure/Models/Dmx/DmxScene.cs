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

    // When true, running steps advance on the beat grid of the currently playing
    // song (per-step Beats count) instead of their fixed Duration. Falls back to
    // Duration whenever no song is playing or its BPM is unknown.
    public bool BpmSyncEnabled { get; set; }

    // Keys that trigger this scene from the DMX desk. Mapped by Npgsql to text[].
    public List<string> KeyBindings { get; set; } = [];

    public virtual ICollection<DmxSceneStep> Steps { get; set; } = [];
}