using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Client.PacketSniffing;

public class GameStateService
{
    public event Action? GameStarted;
    public event Action? GameEnded;

    public event Action<int>? TimeRemainingUpdated;

    public bool GameInProgress = false;
    public int SecondsRemaining { get; set; }

    public void HandleGameStarted()
    {
        GameStarted?.Invoke();

        GameInProgress = true;
    }

    public void HandleGameEnded()
    {
        GameEnded?.Invoke();

        GameInProgress = false;
    }

    public void UpdateTimeRemaining(int secondsRemaining)
    {
        SecondsRemaining = secondsRemaining;

        TimeRemainingUpdated?.Invoke(secondsRemaining);
    }

    public bool IsGameInProgress()
    {
        return GameInProgress;
    }
}
