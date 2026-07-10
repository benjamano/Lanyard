namespace Lanyard.Infrastructure.Models.Dmx;

public class DmxSceneStepChannelValue : CreateAndUpdateBase
{
    public Guid Id { get; set; }

    public Guid SceneStepId { get; set; }
    public DmxSceneStep? SceneStep { get; set; }

    public int ChannelNumber { get; set; }

    public byte Value { get; set; }
}