using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services;

public interface ILaserGameStatusStore
{
    void UpdateStatus(Guid clientId, LaserGameStatusDTO status);
    bool TryGetStatus(Guid clientId, out LaserGameStatusDTO? status);
    IReadOnlyDictionary<Guid, LaserGameStatusDTO> GetAllStatuses();
    void RemoveStatus(Guid clientId);
}
