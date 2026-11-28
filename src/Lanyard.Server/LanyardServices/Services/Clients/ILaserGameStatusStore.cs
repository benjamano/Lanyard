using Lanyard.Shared.DTO;

namespace Lanyard.Application.Services;

public interface ILaserGameStatusStore
{
    void UpdateStatus(Guid clientId, LaserGameStatusDTO status);
    bool TryGetStatus(Guid clientId, out LaserGameStatusDTO? status);
    IReadOnlyDictionary<Guid, LaserGameStatusDTO> GetAllStatuses();
    void RemoveStatus(Guid clientId);

    // The hub marks when it sees a kiosk's game go InGame, so the finished game can be saved with
    // its real start time. Kept here rather than on the DTO because every kiosk report replaces
    // the stored DTO wholesale.
    void MarkGameStarted(Guid clientId, DateTime startedAtUtc);
    bool TryGetGameStart(Guid clientId, out DateTime startedAtUtc);

    // Called once a game's end has been handled, so the next game can never pick up this one's
    // start (for example if the server misses the edge between two games).
    void ClearGameStart(Guid clientId);

    // Raised once a finished game has been saved, so history views can reload straight away
    // instead of polling. The status update for the end edge fires before the save completes, so
    // that event is too early to read the new row.
    void NotifyGameRecorded(Guid clientId);

    event Action<LaserGameStatusDTO>? OnStatusUpdated;
    event Action<Guid>? OnGameRecorded;
}
