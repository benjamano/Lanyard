using Lanyard.Shared.Enum;
using Lanyard.Shared.LaserGame.Capture;
using Lanyard.Shared.LaserGame.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.LaserGame;

[TestClass]
public class LaserReplayRunnerTests
{
    private static readonly byte[] GameStart = Convert.FromHexString("342c403031352c30");
    private static readonly byte[] PlayerScore = Convert.FromHexString("332c312c302c33302c312c33302c332c31302c30");
    private static readonly byte[] GameEnd = Convert.FromHexString("342c403031342c30");

    private GameStateService _state = null!;
    private LaserPacketPipeline _pipeline = null!;
    private int _ended;

    [TestInitialize]
    public void SetUp()
    {
        _state = new GameStateService();
        _pipeline = new LaserPacketPipeline(new LaserGameActions(NullLogger<LaserGameActions>.Instance, _state), _state, NullLogger<LaserPacketPipeline>.Instance);
        _ended = 0;
        _state.GameEnded += () => _ended++;
    }

    private static LaserCapture Game(params (long OffsetMs, byte[] Payload)[] packets)
        => new(new LaserCaptureHeader(), packets.Select(p => new LaserCapturePacket(p.OffsetMs, null, null, p.Payload)).ToList(), null);

    private static readonly LaserCapture WholeGame = Game((0, GameStart), (1000, PlayerScore), (3000, GameEnd));

    [TestMethod]
    public void TryBegin_IsRefusedDuringARealGame()
    {
        _pipeline.Process(LaserPacketSource.Live, GameStart);

        Assert.IsFalse(new LaserReplayRunner(_pipeline).TryBegin());
        Assert.AreEqual(LaserPacketSource.Live, _state.Source);
    }

    [TestMethod]
    public async Task PlaysEveryPacket_WithTheGapsDividedByTheSpeed()
    {
        List<TimeSpan> waits = [];
        LaserReplayRunner runner = new(_pipeline, (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });

        Assert.IsTrue(runner.TryBegin());

        LaserReplayOutcome outcome = await runner.PlayAsync(WholeGame, 2, null, CancellationToken.None);

        Assert.AreEqual(LaserReplayEnd.Completed, outcome.End);
        Assert.AreEqual(3, outcome.PacketsPlayed);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000) }, waits);

        Assert.AreEqual(1, _ended, "The replayed game started and ended through the real pipeline.");
        Assert.AreEqual(GameStatus.NotStarted, _state.GetGameStatus());
        Assert.AreEqual(30, _state.GetPlayersScore(1)?.Score);
        Assert.IsTrue(_state.GetCurrentStatus().IsReplay, "The final scores stay up, marked as a replay.");
    }

    [TestMethod]
    public async Task Instant_DoesNotWait()
    {
        int waits = 0;
        LaserReplayRunner runner = new(_pipeline, (_, _) =>
        {
            waits++;
            return Task.CompletedTask;
        });

        runner.TryBegin();
        await runner.PlayAsync(WholeGame, LaserReplayRunner.Instant, null, CancellationToken.None);

        Assert.AreEqual(0, waits);
    }

    [TestMethod]
    public async Task ReportsProgress()
    {
        List<int> percents = [];
        LaserReplayRunner runner = new(_pipeline);

        runner.TryBegin();
        await runner.PlayAsync(WholeGame, LaserReplayRunner.Instant, new SyncProgress(p => percents.Add(p.Percent)), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 33, 66, 100 }, percents);
    }

    [TestMethod]
    public async Task Stopping_EndsTheReplayPartWay()
    {
        using CancellationTokenSource stop = new();
        LaserReplayRunner runner = new(_pipeline, (wait, token) =>
        {
            stop.Cancel();
            return Task.Delay(Timeout.Infinite, token);
        });

        runner.TryBegin();
        LaserReplayOutcome outcome = await runner.PlayAsync(WholeGame, 1, null, stop.Token);

        Assert.AreEqual(LaserReplayEnd.Stopped, outcome.End);
        Assert.AreEqual(1, outcome.PacketsPlayed);
    }

    [TestMethod]
    public async Task ARealGameStarting_TakesOver()
    {
        LaserReplayRunner runner = new(_pipeline, (wait, token) =>
        {
            // Mid-replay, the arena's control box starts a real game.
            _pipeline.Process(LaserPacketSource.Live, GameStart);
            return Task.Delay(Timeout.Infinite, token);
        });

        runner.TryBegin();
        LaserReplayOutcome outcome = await runner.PlayAsync(WholeGame, 1, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(LaserReplayEnd.TakenOver, outcome.End);
        Assert.AreEqual(LaserPacketSource.Live, _state.Source);
        Assert.AreEqual(GameStatus.InGame, _state.GetGameStatus());
        Assert.IsFalse(_state.GetCurrentStatus().IsReplay);
    }

    [TestMethod]
    public async Task PlayAsync_NeedsTryBeginFirst()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new LaserReplayRunner(_pipeline).PlayAsync(WholeGame, 1, null, CancellationToken.None));
    }

    private sealed class SyncProgress(Action<LaserReplayProgress> report) : IProgress<LaserReplayProgress>
    {
        public void Report(LaserReplayProgress value) => report(value);
    }
}
