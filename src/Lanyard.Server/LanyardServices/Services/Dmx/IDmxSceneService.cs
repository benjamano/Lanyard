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

    /// <summary>How long the step fades in over (0 = snap). The runner keeps it within the step's hold.</summary>
    Task<Result<bool>> UpdateSceneStepFadeAsync(Guid stepId, TimeSpan fade);
    Task<Result<bool>> SaveSceneStepChannelValueAsync(Guid stepId, int channelNumber, byte value);

    /// <summary>Upserts many of a step's channel values in one save (latest wins per channel).</summary>
    Task<Result<bool>> SaveSceneStepChannelValuesAsync(Guid stepId, IReadOnlyList<DmxChannel> values);
    Task<Result<bool>> StopAllScenesForClientAsync(Guid clientId);

    Task<Result<bool>> RenameStepAsync(Guid stepId, string name);

    /// <summary>Copies a step (values included) to just after itself. Returns the copy.</summary>
    Task<Result<DmxSceneStep>> DuplicateStepAsync(Guid stepId);

    /// <summary>Puts the scene's steps in this order; must list every step exactly once.</summary>
    Task<Result<bool>> ReorderStepsAsync(Guid sceneId, IReadOnlyList<Guid> orderedStepIds);

    /// <summary>Replaces every value in the step with these (capture the live look into a step).</summary>
    Task<Result<bool>> ReplaceStepChannelValuesAsync(Guid stepId, IReadOnlyList<DmxChannel> values);

    /// <summary>Takes a channel out of the step, so the step leaves it alone (unlike setting it to 0).</summary>
    Task<Result<bool>> ClearStepChannelAsync(Guid stepId, int channelNumber);

    /// <summary>Names of the active automation rules whose DMX actions use this scene.</summary>
    Task<Result<IReadOnlyList<string>>> GetAutomationRulesUsingSceneAsync(Guid sceneId);
}