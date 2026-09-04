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

        // Fire the transition publish before clearing TimeRemaining/GameLength: the server's
        // edge-triggered game-result recording (SignalRControlHub.UpdateLaserGameStatus ->
        // GameResultService.RecordCompletedGameAsync) reads TotalTimeSeconds off this exact
        // publish to persist the finished game's duration, so the real values must still be in
        // place when GameStateChanged fires here.
        GameStateChanged?.Invoke();

        // Only now clear the countdown, so a later republish during the idle window (e.g. a
        // stray score packet) can't keep echoing this finished game's stale countdown.
        // Deliberately NOT touching CurrentPlayerScores - that persists until HandleGameStarted()
        // by design (see the comment in SignalRControlHub.UpdateLaserGameStatus).
        TimeRemaining = TimeSpan.Zero;
        GameLength = TimeSpan.Zero;
    }

    public void HandleGameGetReady()
    {
        GameStatus = GameStatus.GetReady;
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
            // Accuracy and Team have to be copied across too, not just Score. Every score packet
            // carries a fresh accuracy figure, so only assigning Score froze accuracy at whatever
            // the gun's first packet of the game reported - near-zero, a few shots in.
            existingScore.Score = playerScore.Score;
            existingScore.Accuracy = playerScore.Accuracy;
            existingScore.Team = playerScore.Team;
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
