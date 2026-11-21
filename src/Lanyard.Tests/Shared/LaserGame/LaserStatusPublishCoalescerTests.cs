using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.LaserGame;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.LaserGame;

[TestClass]
public class LaserStatusPublishCoalescerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private static readonly DateTime Start = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    // Stands in for the kiosk's GameStateService.
    private sealed class FakeGame
    {
        public GameStatus Status = GameStatus.NotStarted;
        public int TotalTimeSeconds;
        public int TimeRemainingSeconds;
        public int Score;
        public int Snapshots;

        public LaserGameStatusDTO Snapshot()
        {
            Snapshots++;

            return new LaserGameStatusDTO
            {
                Status = Status,
                TotalTimeSeconds = TotalTimeSeconds,
                TimeRemainingSeconds = TimeRemainingSeconds,
                PlayerCount = 1,
                PlayerScores = [new PlayerScoreDTO { GunId = 1, Score = Score }],
            };
        }
    }

    private static List<LaserGameStatusDTO> Drain(LaserStatusPublishCoalescer coalescer, DateTime now)
    {
        List<LaserGameStatusDTO> sent = [];

        while (coalescer.TakeNext(now, out _) is LaserGameStatusDTO next)
        {
            sent.Add(next);
        }

        return sent;
    }

    [TestMethod]
    public void FirstNotify_IsSentStraightAway()
    {
        FakeGame game = new();
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);

        Assert.IsTrue(coalescer.Notify(game.Status));

        Assert.AreEqual(1, Drain(coalescer, Start).Count);
    }

    [TestMethod]
    public void ScoreChanges_AreMergedIntoOneSendPerInterval_AndTheLatestWins()
    {
        FakeGame game = new() { Status = GameStatus.InGame };
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);
        coalescer.Notify(game.Status);
        Drain(coalescer, Start);

        List<LaserGameStatusDTO> sent = [];
        DateTime now = Start;

        // 100 score packets over one second, 10 ms apart, with the send loop checking each time.
        for (int i = 1; i <= 100; i++)
        {
            now = Start.AddMilliseconds(i * 10);
            game.Score = i;
            coalescer.Notify(game.Status);
            sent.AddRange(Drain(coalescer, now));
        }

        sent.AddRange(Drain(coalescer, now + Interval));

        Assert.IsTrue(sent.Count <= 5, $"Expected at most 5 sends in a second, got {sent.Count}.");
        Assert.AreEqual(100, sent[^1].PlayerScores[0].Score);
    }

    [TestMethod]
    public void MergedChange_WaitsForItsSlot_AndSaysHowLong()
    {
        FakeGame game = new() { Status = GameStatus.InGame };
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);
        coalescer.Notify(game.Status);
        Drain(coalescer, Start);

        game.Score = 10;
        Assert.IsTrue(coalescer.Notify(game.Status), "The first merged change wakes the send loop.");
        Assert.IsFalse(coalescer.Notify(game.Status), "Later ones don't need to.");

        Assert.IsNull(coalescer.TakeNext(Start.AddMilliseconds(100), out TimeSpan? retryAfter));
        Assert.AreEqual(TimeSpan.FromMilliseconds(150), retryAfter);

        LaserGameStatusDTO? sent = coalescer.TakeNext(Start.AddMilliseconds(250), out _);
        Assert.AreEqual(10, sent?.PlayerScores[0].Score);
    }

    [TestMethod]
    public void NothingChanged_NothingToSend()
    {
        FakeGame game = new();
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);

        Assert.IsNull(coalescer.TakeNext(Start, out TimeSpan? retryAfter));
        Assert.IsNull(retryAfter);
    }

    [TestMethod]
    public void StatusChanges_InsideOneInterval_AreAllSentInOrder()
    {
        FakeGame game = new();
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);
        coalescer.Notify(game.Status);
        Drain(coalescer, Start);

        foreach (GameStatus status in new[] { GameStatus.GetReady, GameStatus.InGame, GameStatus.NotStarted })
        {
            game.Status = status;
            coalescer.Notify(status);
        }

        List<LaserGameStatusDTO> sent = Drain(coalescer, Start.AddMilliseconds(1));

        CollectionAssert.AreEqual(
            new[] { GameStatus.GetReady, GameStatus.InGame, GameStatus.NotStarted },
            sent.Select(x => x.Status).ToArray());
    }

    [TestMethod]
    public void GameEnd_KeepsTheFinalScoresAndLength_ThatAreClearedStraightAfter()
    {
        FakeGame game = new() { Status = GameStatus.InGame, TotalTimeSeconds = 600, TimeRemainingSeconds = 1, Score = 4200 };
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);
        coalescer.Notify(game.Status);
        Drain(coalescer, Start);

        // As GameStateService.HandleGameEnded: status flips, GameStateChanged fires, then it clears.
        game.Status = GameStatus.NotStarted;
        coalescer.Notify(game.Status);
        game.TotalTimeSeconds = 0;
        game.TimeRemainingSeconds = 0;
        game.Score = 0;

        LaserGameStatusDTO? ended = coalescer.TakeNext(Start.AddMilliseconds(1), out _);

        Assert.AreEqual(GameStatus.NotStarted, ended?.Status);
        Assert.AreEqual(600, ended!.TotalTimeSeconds);
        Assert.AreEqual(4200, ended.PlayerScores[0].Score);
    }

    [TestMethod]
    public void StatusChange_IsSentEvenStraightAfterAnotherSend()
    {
        FakeGame game = new() { Status = GameStatus.InGame };
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);
        coalescer.Notify(game.Status);
        Drain(coalescer, Start);

        game.Status = GameStatus.NotStarted;
        coalescer.Notify(game.Status);

        Assert.IsNotNull(coalescer.TakeNext(Start.AddMilliseconds(1), out _), "A status change doesn't wait for the interval.");
    }

    [TestMethod]
    public void StatusChange_MakesAPendingMergedChangeRedundant()
    {
        FakeGame game = new() { Status = GameStatus.InGame };
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);
        coalescer.Notify(game.Status);
        Drain(coalescer, Start);

        game.Score = 50;
        coalescer.Notify(game.Status);
        game.Status = GameStatus.NotStarted;
        coalescer.Notify(game.Status);

        List<LaserGameStatusDTO> sent = Drain(coalescer, Start.AddSeconds(1));

        Assert.AreEqual(1, sent.Count);
        Assert.AreEqual(50, sent[0].PlayerScores[0].Score);
    }

    [TestMethod]
    public void SendNow_QueuesTheCurrentState()
    {
        FakeGame game = new();
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);
        coalescer.Notify(game.Status);
        Drain(coalescer, Start);

        coalescer.SendNow();

        Assert.IsNotNull(coalescer.TakeNext(Start.AddMilliseconds(1), out _));
    }

    [TestMethod]
    public void MergedChanges_DoNotSnapshotOnEveryPacket()
    {
        FakeGame game = new() { Status = GameStatus.InGame };
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);
        coalescer.Notify(game.Status);

        for (int i = 0; i < 1000; i++)
        {
            coalescer.Notify(game.Status);
        }

        Assert.AreEqual(1, game.Snapshots);
    }

    [TestMethod]
    public void TakeCounters_CountsChangesAndSends_ThenResets()
    {
        FakeGame game = new() { Status = GameStatus.InGame };
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);

        for (int i = 0; i < 10; i++)
        {
            coalescer.Notify(game.Status);
        }

        Drain(coalescer, Start);
        Drain(coalescer, Start.AddSeconds(1));

        Assert.AreEqual((10, 2), coalescer.TakeCounters());
        Assert.AreEqual((0, 0), coalescer.TakeCounters());
    }

    [TestMethod]
    public void RandomGames_NeverLoseAStatusEdge()
    {
        Random random = new(1234);
        FakeGame game = new();
        LaserStatusPublishCoalescer coalescer = new(game.Snapshot, Interval);

        List<GameStatus> notifiedEdges = [];
        List<GameStatus> sentStatuses = [];
        DateTime now = Start;

        for (int i = 0; i < 20_000; i++)
        {
            now = now.AddMilliseconds(random.Next(0, 30));

            if (random.Next(50) == 0)
            {
                game.Status = (GameStatus)random.Next((int)GameStatus.NotStarted, (int)GameStatus.GetReady + 1);
            }

            if (notifiedEdges.Count == 0 || notifiedEdges[^1] != game.Status)
            {
                notifiedEdges.Add(game.Status);
            }

            coalescer.Notify(game.Status);

            // The send loop doesn't run after every packet.
            if (random.Next(3) == 0)
            {
                sentStatuses.AddRange(Drain(coalescer, now).Select(x => x.Status));
            }
        }

        sentStatuses.AddRange(Drain(coalescer, now + Interval).Select(x => x.Status));

        List<GameStatus> sentEdges = [];
        foreach (GameStatus status in sentStatuses)
        {
            if (sentEdges.Count == 0 || sentEdges[^1] != status)
            {
                sentEdges.Add(status);
            }
        }

        CollectionAssert.AreEqual(notifiedEdges, sentEdges);
    }
}
