using Lanyard.Infrastructure.DTO;

/// <summary>
/// The system-level scene runner (a singleton). Like <see cref="IDmxService"/> it does not check
/// who is calling; code acting for a signed-in user goes through <see cref="IDmxDeskService"/>.
/// </summary>
public interface IDmxSceneRunnerService
{
    // (clientId, sceneId). Raised once per run; a restart of a running scene raises Started again
    // without a Stopped in between, so subscribers should treat running scenes as a set.
    event Action<Guid, Guid>? OnSceneStarted;
    event Action<Guid, Guid>? OnSceneStopped;

    // (clientId, sceneId, 1-based position of the step in the scene)
    event Action<Guid, Guid, int>? OnSceneStepAdvanced;

    // (clientId, sceneId, reason). Raised just before OnSceneStopped when playback faulted.
    event Action<Guid, Guid, string>? OnSceneFailed;

    // holdFor auto-stops the scene after the given time, via the same cancellation
    // path as StopScene. Null runs until stopped.
    Task<Result<bool>> StartSceneAsync(Guid clientId, Guid sceneId, TimeSpan? holdFor = null);
    Result<bool> StopScene(Guid sceneId);
    Result<bool> StopAllScenesForClient(Guid clientId);
    List<Guid> GetRunningSceneIds(Guid clientId);
}
