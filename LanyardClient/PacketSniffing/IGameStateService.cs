using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;

public interface IGameStateService
{
    event Action? GameStarted;
    event Action? GameEnded;

    event Action<PlayerHitDTO>? PlayerHit;

    event Action<TimeSpan>? TimeRemainingUpdated;

    void HandleGameStarted();
    void HandleGameEnded();
    void HandlePlayerHit(int shotGunId, int shotByGunId);

    void UpdateTimeRemaining(TimeSpan timeRemaining);

    GameStatus GetGameStatus();
    TimeSpan GetTimeRemaining();

    PlayerScoreDTO? GetPlayersScore(int gunId);
    void UpdatePlayerScore(PlayerScoreDTO playerScore);

    void UpdateGameLength(TimeSpan gameLength);
}