using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;

namespace Lanyard.Client.PacketSniffing;

// Packets change this state on the capture thread while the status publisher snapshots it from its
// own loop, so every read and write takes _lock. Events are raised after the lock is released:
// the publisher snapshots inside GameStateChanged, and that takes the lock again.
public class GameStateService : IGameStateService
{
    public event Action? GameStateChanged;
    public event Action? GameStarted;
    public event Action? GameEnded;

    public event Action<PlayerHitDTO>? PlayerHit;

    public event Action<TimeSpan>? TimeRemainingUpdated;

    private readonly object _lock = new();

    private GameStatus _gameStatus = GameStatus.NotStarted;

    private TimeSpan _gameLength = TimeSpan.Zero;
    private TimeSpan _timeRemaining = TimeSpan.Zero;

    private List<PlayerScoreDTO> _currentPlayerScores = [];

    public void HandleGameStarted()
    {
        GameStarted?.Invoke();

        lock (_lock)
        {
            _gameStatus = GameStatus.InGame;
            _currentPlayerScores = [];
        }

        GameStateChanged?.Invoke();
    }

    public void HandleGameEnded()
    {
        GameEnded?.Invoke();

        lock (_lock)
        {
            _gameStatus = GameStatus.NotStarted;
        }

        // Fire the transition publish before clearing TimeRemaining/GameLength: the server's
        // edge-triggered game-result recording (SignalRControlHub.UpdateLaserGameStatus ->
        // GameResultService.RecordCompletedGameAsync) reads TotalTimeSeconds off this exact
        // publish to persist the finished game's duration, so the real values must still be in
        // place when GameStateChanged fires here. The publisher snapshots status changes inside
        // this call, so the snapshot it sends keeps them.
        GameStateChanged?.Invoke();

        // Only now clear the countdown, so a later republish during the idle window (e.g. a
        // stray score packet) can't keep echoing this finished game's stale countdown.
        // Deliberately NOT touching CurrentPlayerScores - that persists until HandleGameStarted()
        // by design (see the comment in SignalRControlHub.UpdateLaserGameStatus).
        lock (_lock)
        {
            _timeRemaining = TimeSpan.Zero;
            _gameLength = TimeSpan.Zero;
        }
    }

    public void HandleGameGetReady()
    {
        lock (_lock)
        {
            _gameStatus = GameStatus.GetReady;
        }

        GameStateChanged?.Invoke();
    }

    public void HandlePlayerHit(int shotGunId, int shotByGunId)
    {
        PlayerHit?.Invoke(new PlayerHitDTO { ShotByGunId = shotByGunId, ShotGunId = shotGunId });
        GameStateChanged?.Invoke();
    }

    public void UpdateTimeRemaining(TimeSpan timeRemaining)
    {
        lock (_lock)
        {
            _timeRemaining = timeRemaining;
        }

        TimeRemainingUpdated?.Invoke(timeRemaining);
        GameStateChanged?.Invoke();
    }

    public TimeSpan GetTimeRemaining()
    {
        lock (_lock)
        {
            return _timeRemaining;
        }
    }

    public TimeSpan GetTotalGameTime()
    {
        return GetGameLength();
    }

    public GameStatus GetGameStatus()
    {
        lock (_lock)
        {
            return _gameStatus;
        }
    }

    public LaserGameStatusDTO GetCurrentStatus()
    {
        lock (_lock)
        {
            // Copies, not the live entries: a snapshot can sit in the publisher's queue while
            // later score packets update the same guns.
            List<PlayerScoreDTO> playerScores = _currentPlayerScores.Select(Copy).ToList();

            return new LaserGameStatusDTO
            {
                Status = _gameStatus,
                TimeRemainingSeconds = (int)Math.Max(0, _timeRemaining.TotalSeconds),
                TotalTimeSeconds = (int)Math.Max(0, _gameLength.TotalSeconds),
                PlayerCount = playerScores.Count,
                PlayerScores = playerScores,
                LastUpdateUtc = DateTime.UtcNow
            };
        }
    }

    public PlayerScoreDTO? GetPlayersScore(int gunId)
    {
        lock (_lock)
        {
            PlayerScoreDTO? score = _currentPlayerScores.Find(x => x.GunId == gunId);

            return score == null ? null : Copy(score);
        }
    }

    public List<PlayerScoreDTO> GetAllPlayerScores()
    {
        lock (_lock)
        {
            return _currentPlayerScores.Select(Copy).ToList();
        }
    }

    public void UpdatePlayerScore(PlayerScoreDTO playerScore)
    {
        lock (_lock)
        {
            PlayerScoreDTO? existingScore = _currentPlayerScores.Find(x => x.GunId == playerScore.GunId);
            if (existingScore != null)
            {
                // Accuracy and Team have to be copied across too, not just Score. Every score packet
                // carries a fresh accuracy figure, so only assigning Score froze accuracy at whatever
                // the gun's first packet of the game reported - near-zero, a few shots in.
                existingScore.Score = playerScore.Score;
                existingScore.Accuracy = playerScore.Accuracy;
                existingScore.Team = playerScore.Team;
            }
            else
            {
                _currentPlayerScores.Add(playerScore);
            }
        }

        GameStateChanged?.Invoke();
    }

    public void UpdateGameLength(TimeSpan gameLength)
    {
        lock (_lock)
        {
            _gameLength = gameLength;
        }

        GameStateChanged?.Invoke();
    }

    public TimeSpan GetGameLength()
    {
        lock (_lock)
        {
            return _gameLength;
        }
    }

    private static PlayerScoreDTO Copy(PlayerScoreDTO score) => new()
    {
        GunId = score.GunId,
        GunName = score.GunName,
        Team = score.Team,
        Score = score.Score,
        Accuracy = score.Accuracy,
    };
}
