using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.Models.Dmx;

public interface IDmxSceneService
{
    Task<Result<IEnumerable<DmxSceneDTO>>> GetScenesForClientAsync(Guid clientId);

    Task<Result<DmxScene>> CreateSceneAsync(Guid clientId, string name);
    Task<Result<DmxSceneDTO>> GetSceneByIdAsync(Guid sceneId, Guid? clientId = null);
    Task<Result<bool>> UpdateSceneAsync(DmxScene scene);
    Task<Result<bool>> DeleteSceneAsync(Guid sceneId);
    Task<Result<List<DmxSceneStep>>> GetSceneStepsAsync(Guid sceneId);
    Task<Result<bool>> DeleteSceneStepAsync(Guid stepId);
    Task<Result<DmxSceneStep>> CreateSceneStepAsync(Guid sceneId);
    Task<Result<bool>> UpdateSceneStepDurationAsync(Guid stepId, TimeSpan newDuration);
    Task<Result<bool>> UpdateSceneStepBeatsAsync(Guid stepId, double beats);
    Task<Result<bool>> SaveSceneStepChannelValueAsync(Guid stepId, int channelNumber, byte value);
    Task<Result<bool>> StopAllScenesForClientAsync(Guid clientId);
}