using Lanyard.Infrastructure.Models.Dmx;

namespace Lanyard.Infrastructure.DTO.Dmx;

public class DmxSceneDTO : DmxScene
{
    public bool IsRunning { get; set; }

    public int StepCount { get; set; }
}