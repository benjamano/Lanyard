using Lanyard.Application.Services.Projection;
using Lanyard.Infrastructure.Enum;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Projection;

[TestClass]
public class GameEventWaitTests
{
    private static readonly Guid Kiosk = Guid.NewGuid();

    private static LaserGameStatusDTO Status(GameStatus status, int timeRemainingSeconds = 0, Guid? clientId = null) => new()
    {
        ClientId = clientId ?? Kiosk,
        Status = status,
        TimeRemainingSeconds = timeRemainingSeconds
    };

    private static GameEventWait Wait(ProjectionGameEvent gameEvent, int? secondsLeft = null) => new(gameEvent, Kiosk, secondsLeft);

    [TestMethod]
    public void NoStatus_IsNeverSatisfied()
    {
        foreach (ProjectionGameEvent gameEvent in GameWaitStepNames.Events)
        {
            GameEventWait wait = Wait(gameEvent, 30);
            wait.Observe(null);
            Assert.IsFalse(wait.Satisfied, gameEvent.ToString());
        }
    }

    [TestMethod]
    public void OtherKiosksPublishes_AreIgnored()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.GameStarts);

        wait.Observe(Status(GameStatus.NotStarted));
        wait.Observe(Status(GameStatus.InGame, clientId: Guid.NewGuid()));

        Assert.IsFalse(wait.Satisfied);
    }

    [TestMethod]
    public void GetReady_EndsOnGetReady_OrStraightIntoAGame()
    {
        GameEventWait viaGetReady = Wait(ProjectionGameEvent.GetReady);
        viaGetReady.Observe(Status(GameStatus.NotStarted));
        Assert.IsFalse(viaGetReady.Satisfied);
        viaGetReady.Observe(Status(GameStatus.GetReady));
        Assert.IsTrue(viaGetReady.Satisfied);

        // A missed Get ready mustn't cost a whole game.
        GameEventWait viaGame = Wait(ProjectionGameEvent.GetReady);
        viaGame.Observe(Status(GameStatus.NotStarted));
        viaGame.Observe(Status(GameStatus.InGame));
        Assert.IsTrue(viaGame.Satisfied);
    }

    [TestMethod]
    public void GetReady_EndsAtOnce_WhenAlreadyGettingReadyOrPlaying()
    {
        GameEventWait ready = Wait(ProjectionGameEvent.GetReady);
        ready.Observe(Status(GameStatus.GetReady));
        Assert.IsTrue(ready.Satisfied);

        GameEventWait playing = Wait(ProjectionGameEvent.GetReady);
        playing.Observe(Status(GameStatus.InGame));
        Assert.IsTrue(playing.Satisfied);
    }

    [TestMethod]
    public void GameStarts_IgnoresGetReady_AndEndsWhenTheGameIsOn()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.GameStarts);

        wait.Observe(Status(GameStatus.NotStarted));
        wait.Observe(Status(GameStatus.GetReady));
        Assert.IsFalse(wait.Satisfied);

        wait.Observe(Status(GameStatus.InGame));
        Assert.IsTrue(wait.Satisfied);
    }

    [TestMethod]
    public void GameStarts_EndsAtOnce_WhenAGameIsAlreadyRunning()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.GameStarts);
        wait.Observe(Status(GameStatus.InGame));
        Assert.IsTrue(wait.Satisfied);
    }

    [TestMethod]
    public void GameEnds_NeedsTheRunningGameToFinish()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.GameEnds);

        wait.Observe(Status(GameStatus.InGame, 300));
        Assert.IsFalse(wait.Satisfied);

        // Republishes during the game change nothing.
        wait.Observe(Status(GameStatus.InGame, 200));
        Assert.IsFalse(wait.Satisfied);

        wait.Observe(Status(GameStatus.NotStarted));
        Assert.IsTrue(wait.Satisfied);
    }

    [TestMethod]
    public void GameEnds_EndsAtOnce_WhenNoGameIsRunning()
    {
        GameEventWait idle = Wait(ProjectionGameEvent.GameEnds);
        idle.Observe(Status(GameStatus.NotStarted));
        Assert.IsTrue(idle.Satisfied);

        GameEventWait gettingReady = Wait(ProjectionGameEvent.GameEnds);
        gettingReady.Observe(Status(GameStatus.GetReady));
        Assert.IsTrue(gettingReady.Satisfied);
    }

    [TestMethod]
    public void GameEnds_SeesTheEnd_AfterTheKioskDropsOutAndComesBack()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.GameEnds);

        wait.Observe(Status(GameStatus.InGame, 120));

        // Disconnected: the store has nothing, which changes nothing here.
        wait.Observe(null);
        Assert.IsFalse(wait.Satisfied);

        wait.Observe(Status(GameStatus.NotStarted));
        Assert.IsTrue(wait.Satisfied);
    }

    [TestMethod]
    public void TimeLeftBelow_EndsWhenTheCountdownReachesTheThreshold()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.TimeLeftBelow, secondsLeft: 60);

        wait.Observe(Status(GameStatus.NotStarted));
        wait.Observe(Status(GameStatus.InGame, 61));
        Assert.IsFalse(wait.Satisfied);

        wait.Observe(Status(GameStatus.InGame, 60));
        Assert.IsTrue(wait.Satisfied);
    }

    [TestMethod]
    public void TimeLeftBelow_IgnoresTheZeroOfAGameThatHasOnlyJustStarted()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.TimeLeftBelow, secondsLeft: 60);

        wait.Observe(Status(GameStatus.NotStarted));

        // The kiosk's first In game publish carries 0 left until the countdown arrives.
        wait.Observe(Status(GameStatus.InGame, 0));
        Assert.IsFalse(wait.Satisfied);

        wait.Observe(Status(GameStatus.InGame, 600));
        Assert.IsFalse(wait.Satisfied);
    }

    [TestMethod]
    public void TimeLeftBelow_EndsAtOnce_WhenAlreadyBelow()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.TimeLeftBelow, secondsLeft: 60);
        wait.Observe(Status(GameStatus.InGame, 30));
        Assert.IsTrue(wait.Satisfied);
    }

    [TestMethod]
    public void TimeLeftBelow_EndsWhenTheGameStopsEarly()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.TimeLeftBelow, secondsLeft: 30);

        wait.Observe(Status(GameStatus.InGame, 400));
        wait.Observe(Status(GameStatus.NotStarted));

        Assert.IsTrue(wait.Satisfied);
    }

    [TestMethod]
    public void TimeLeftBelow_WaitsForAGame_WhenIdle()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.TimeLeftBelow, secondsLeft: 30);

        wait.Observe(Status(GameStatus.NotStarted));
        Assert.IsFalse(wait.Satisfied);
    }

    [TestMethod]
    public void Satisfied_Latches()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.GameStarts);

        wait.Observe(Status(GameStatus.InGame));
        wait.Observe(Status(GameStatus.NotStarted));

        Assert.IsTrue(wait.Satisfied);
    }
}
