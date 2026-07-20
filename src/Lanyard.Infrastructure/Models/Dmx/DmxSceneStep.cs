namespace Lanyard.Infrastructure.Models.Dmx;

public class DmxSceneStep : CreateAndUpdateBase
{
    public Guid Id { get; set; }

    public Guid SceneId { get; set; }
    public DmxScene? Scene { get; set; }

    public int StepNumber { get; set; }

    public string Name { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }

    // Beats this step holds when the scene is BPM-synced. Double so 0.5 gives
    // eighth-note chases; clamped to [0.25, 64] at the service boundary.
    public double Beats { get; set; } = 1;

    public virtual ICollection<DmxSceneStepChannelValue> ChannelValues { get; set; } = [];
}
