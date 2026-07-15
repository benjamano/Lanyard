using Lanyard.Infrastructure.DTO;

public interface IDmxSceneRunnerService
{
    event Action<Guid, Guid>? OnSceneStarted;
    event Action<Guid, Guid>? OnSceneStopped;
    event Action<Guid, Guid, int>? OnSceneStepAdvanced;

    // holdFor auto-stops the scene after the given time, via the same cancellation
    // path as StopScene. Null runs until stopped.
    Task<Result<bool>> StartSceneAsync(Guid clientId, Guid sceneId, TimeSpan? holdFor = null);
    Result<bool> StopScene(Guid sceneId);
    Result<bool> StopAllScenesForClient(Guid clientId);
    List<Guid> GetRunningSceneIds(Guid clientId);
}
