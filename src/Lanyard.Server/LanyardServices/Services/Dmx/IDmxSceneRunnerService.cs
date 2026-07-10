using Lanyard.Infrastructure.DTO;

public interface IDmxSceneRunnerService
{
    event Action<Guid, Guid>? OnSceneStarted;
    event Action<Guid, Guid>? OnSceneStopped;
    event Action<Guid, Guid, int>? OnSceneStepAdvanced;

    Task<Result<bool>> StartSceneAsync(Guid clientId, Guid sceneId);
    Result<bool> StopScene(Guid sceneId);
    Result<bool> StopAllScenesForClient(Guid clientId);
    List<Guid> GetRunningSceneIds(Guid clientId);
}
