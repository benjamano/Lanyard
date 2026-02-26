using Lanyard.Shared.DTO;
using System.Collections.Concurrent;

namespace Lanyard.Application.Services;

public class LaserGameStatusStore : ILaserGameStatusStore
{
    private readonly ConcurrentDictionary<Guid, LaserGameStatusDTO> _statusByClientId = new();

    public void UpdateStatus(Guid clientId, LaserGameStatusDTO status)
    {
        status.ClientId = clientId;
        status.LastUpdateUtc = DateTime.UtcNow;

        _statusByClientId.AddOrUpdate(clientId, status, (_, _) => status);
    }

    public bool TryGetStatus(Guid clientId, out LaserGameStatusDTO? status)
    {
        bool found = _statusByClientId.TryGetValue(clientId, out LaserGameStatusDTO? currentStatus);
        status = currentStatus;
        return found;
    }

    public IReadOnlyDictionary<Guid, LaserGameStatusDTO> GetAllStatuses()
    {
        return _statusByClientId;
    }

    public void RemoveStatus(Guid clientId)
    {
        _statusByClientId.TryRemove(clientId, out _);
    }
}
