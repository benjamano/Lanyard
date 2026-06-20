using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;

namespace Lanyard.Client.PacketSniffing;

public class GameStateService : IGameStateService
{
    public event Action? GameStateChanged;
    public event Action? GameStarted;
    public event Action? GameEnded;

    public event Action<PlayerHitDTO>? PlayerHit; 

    public event Action<TimeSpan>? TimeRemainingUpdated;

    public GameStatus GameStatus = GameStatus.NotStarted;

    public TimeSpan GameLength = TimeSpan.Zero;
    public TimeSpan TimeRemaining = TimeSpan.Zero;

    public List<PlayerScoreDTO> CurrentPlayerScores = [];

    public void HandleGameStarted()
    {
        GameStarted?.Invoke();

        GameStatus = GameStatus.InGame;

        CurrentPlayerScores = [];
        GameStateChanged?.Invoke();
    }

    public void HandleGameEnded()
    {
        GameEnded?.Invoke();

        GameStatus = GameStatus.NotStarted;
        GameStateChanged?.Invoke();
    }

    public void HandlePlayerHit(int shotGunId, int shotByGunId)
    {
        PlayerHit?.Invoke(new PlayerHitDTO { ShotByGunId = shotByGunId, ShotGunId = shotGunId });
        GameStateChanged?.Invoke();
    }

    public void UpdateTimeRemaining(TimeSpan timeRemaining)
    {
        TimeRemaining = timeRemaining;

        TimeRemainingUpdated?.Invoke(timeRemaining);
        GameStateChanged?.Invoke();
    }

    public TimeSpan GetTimeRemaining()
    {
        return TimeRemaining;
    }

    public TimeSpan GetTotalGameTime()
    {
        return GameLength;
    }

    public GameStatus GetGameStatus()
    {
        return GameStatus;
    }

    public LaserGameStatusDTO GetCurrentStatus()
    {
        List<PlayerScoreDTO> playerScores = CurrentPlayerScores.ToList();

        return new LaserGameStatusDTO
        {
            Status = GameStatus,
            TimeRemainingSeconds = (int)Math.Max(0, TimeRemaining.TotalSeconds),
            TotalTimeSeconds = (int)Math.Max(0, GameLength.TotalSeconds),
            PlayerCount = playerScores.Count,
            PlayerScores = playerScores,
            LastUpdateUtc = DateTime.UtcNow
        };
    }

    public PlayerScoreDTO? GetPlayersScore(int gunId)
    {
        return CurrentPlayerScores.Find(x => x.GunId == gunId);
    }

    public List<PlayerScoreDTO> GetAllPlayerScores()
    {
        return CurrentPlayerScores;
    }

    public void UpdatePlayerScore(PlayerScoreDTO playerScore)
    {
        PlayerScoreDTO? existingScore = CurrentPlayerScores.Find(x => x.GunId == playerScore.GunId);
        if (existingScore != null)
        {
            existingScore.Score = playerScore.Score;
        }
        else
        {
            CurrentPlayerScores.Add(playerScore);
        }

        GameStateChanged?.Invoke();
    }

    public void UpdateGameLength(TimeSpan gameLength)
    {
        GameLength = gameLength;
        GameStateChanged?.Invoke();
    }

    public TimeSpan GetGameLength()
    {
        return GameLength;
    }
}
