public interface IGameStateService
{
    event Action? GameStarted;
    event Action? GameEnded;

    event Action<int>? TimeRemainingUpdated;

    void HandleGameStarted();
    void HandleGameEnded();
    void UpdateTimeRemaining(int secondsRemaining);

    bool IsGameInProgress();
}