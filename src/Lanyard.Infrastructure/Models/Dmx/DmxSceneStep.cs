namespace Lanyard.Infrastructure.Models.Dmx;

public class DmxSceneStep : CreateAndUpdateBase
{
    public Guid Id { get; set; }

    public Guid SceneId { get; set; }
    public DmxScene? Scene { get; set; }

    public int StepNumber { get; set; }

    public string Name { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }

    public virtual ICollection<DmxSceneStepChannelValue> ChannelValues { get; set; } = [];
}
