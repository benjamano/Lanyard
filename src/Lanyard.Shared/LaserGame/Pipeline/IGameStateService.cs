using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;

namespace Lanyard.Shared.LaserGame.Pipeline;

public interface IGameStateService
{
    event Action? GameStateChanged;
    event Action? GameStarted;
    event Action? GameEnded;

    event Action<PlayerHitDTO>? PlayerHit;

    event Action<TimeSpan>? TimeRemainingUpdated;

    /// <summary>Where the packets driving this state come from: the arena, or a recording being replayed.</summary>
    LaserPacketSource Source { get; }

    void HandleGameStarted();
    void HandleGameEnded();
    void HandleGameGetReady();
    void HandlePlayerHit(int shotGunId, int shotByGunId);

    void UpdateTimeRemaining(TimeSpan timeRemaining);

    /// <summary>
    /// Drops the current game, scores and countdown without raising GameEnded, and sets where
    /// the next packets come from. Used when switching between live packets and a replay, so
    /// neither inherits the other's half-finished game.
    /// </summary>
    void Reset(LaserPacketSource source);

    GameStatus GetGameStatus();
    TimeSpan GetTimeRemaining();

    LaserGameStatusDTO GetCurrentStatus();

    PlayerScoreDTO? GetPlayersScore(int gunId);
    List<PlayerScoreDTO> GetAllPlayerScores();
    void UpdatePlayerScore(PlayerScoreDTO playerScore);

    void UpdateGameLength(TimeSpan gameLength);
    TimeSpan GetGameLength();
}
