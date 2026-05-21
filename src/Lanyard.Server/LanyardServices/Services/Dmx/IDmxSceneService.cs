using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.Models.Dmx;

public interface IDmxSceneService
{
    event Action<Guid, Guid>? OnSceneStarted;
    event Action<Guid, Guid>? OnSceneStopped;

    Task<Result<IEnumerable<DmxSceneDTO>>> GetScenesForClientAsync(Guid clientId);

    Task<Result<DmxScene>> CreateSceneAsync(Guid clientId, string name);
}