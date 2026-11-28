using Lanyard.Shared.DTO;
using System.Collections.Concurrent;

namespace Lanyard.Application.Services;

public class LaserGameStatusStore : ILaserGameStatusStore
{
    public event Action<LaserGameStatusDTO>? OnStatusUpdated;
    public event Action<Guid>? OnGameRecorded;

    private readonly ConcurrentDictionary<Guid, LaserGameStatusDTO> _statusByClientId = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _gameStartedUtcByClientId = new();

    public void UpdateStatus(Guid clientId, LaserGameStatusDTO status)
    {
        status.ClientId = clientId;
        status.LastUpdateUtc = DateTime.UtcNow;

        _statusByClientId.AddOrUpdate(clientId, status, (_, _) => status);

        OnStatusUpdated?.Invoke(status);
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
        _gameStartedUtcByClientId.TryRemove(clientId, out _);
    }

    public void MarkGameStarted(Guid clientId, DateTime startedAtUtc)
    {
        _gameStartedUtcByClientId[clientId] = startedAtUtc;
    }

    public bool TryGetGameStart(Guid clientId, out DateTime startedAtUtc)
    {
        return _gameStartedUtcByClientId.TryGetValue(clientId, out startedAtUtc);
    }

    public void NotifyGameRecorded(Guid clientId)
    {
        OnGameRecorded?.Invoke(clientId);
    }
}
