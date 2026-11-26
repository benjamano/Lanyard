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

    // Each status is stamped a millisecond after the last, as the store stamps real publishes.
    private static DateTime _clock = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private static LaserGameStatusDTO Status(GameStatus status, int timeRemainingSeconds = 0, Guid? clientId = null) => new()
    {
        ClientId = clientId ?? Kiosk,
        Status = status,
        TimeRemainingSeconds = timeRemainingSeconds,
        LastUpdateUtc = _clock = _clock.AddMilliseconds(1)
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
    }

    [TestMethod]
    public void GameEnds_ReachedDuringGetReady_WaitsForThatGameToFinish()
    {
        // Wait Get ready -> short scoreboard step -> Wait Game ends: the kiosk is usually still in
        // Get ready when the second wait begins.
        GameEventWait wait = Wait(ProjectionGameEvent.GameEnds);

        wait.Observe(Status(GameStatus.GetReady));
        Assert.IsFalse(wait.Satisfied, "Get ready means a game is about to run.");

        wait.Observe(Status(GameStatus.InGame, 600));
        Assert.IsFalse(wait.Satisfied);

        wait.Observe(Status(GameStatus.NotStarted));
        Assert.IsTrue(wait.Satisfied);
    }

    [TestMethod]
    public void GameEnds_EndsWhenGetReadyIsCalledOff()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.GameEnds);

        wait.Observe(Status(GameStatus.GetReady));
        wait.Observe(Status(GameStatus.NotStarted));

        Assert.IsTrue(wait.Satisfied);
    }

    [TestMethod]
    public void AnOlderStatusArrivingLate_IsIgnored()
    {
        GameEventWait wait = Wait(ProjectionGameEvent.TimeLeftBelow, secondsLeft: 30);

        LaserGameStatusDTO readByTheTick = Status(GameStatus.GetReady);
        wait.Observe(readByTheTick);

        // The publish that started the game is heard first...
        wait.Observe(Status(GameStatus.InGame, 600));

        // ...then the tick's stale read lands: it mustn't look like the game just ended.
        wait.Observe(readByTheTick);

        Assert.IsFalse(wait.Satisfied);
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
