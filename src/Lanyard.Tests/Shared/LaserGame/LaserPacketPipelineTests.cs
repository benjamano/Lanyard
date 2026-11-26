using System.Text;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.LaserGame.Capture;
using Lanyard.Shared.LaserGame.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.LaserGame;

/// <summary>The kiosk's real decode, dispatch and game state path, as moved into Lanyard.Shared.</summary>
[TestClass]
public class LaserPacketPipelineTests
{
    // Real Begeara 2 packets (see LaserPacketDecoderTests).
    private static readonly byte[] GameStart = Convert.FromHexString("342c403031352c30");                          // 4,@015,0
    private static readonly byte[] GameEnd = Convert.FromHexString("342c403031342c30");                            // 4,@014,0
    private static readonly byte[] Timing = Convert.FromHexString("312c373537332c2240303034222c3630302c31");      // 1,7573,"@004",600,1
    private static readonly byte[] PlayerScore = Convert.FromHexString("332c312c302c33302c312c33302c332c31302c30"); // 3,1,0,30,1,30,3,10,0
    private static readonly byte[] ShotConfirmed = Convert.FromHexString("352c31312c312c322c302c302c302c30");     // 5,11,1,2,0,0,0,0

    private GameStateService _state = null!;
    private LaserPacketPipeline _pipeline = null!;

    private int _started;
    private int _ended;
    private int _changed;
    private readonly List<PlayerHitDTO> _hits = [];

    [TestInitialize]
    public void SetUp()
    {
        _started = 0;
        _ended = 0;
        _changed = 0;
        _hits.Clear();

        _state = new GameStateService();
        _pipeline = new LaserPacketPipeline(new LaserGameActions(NullLogger<LaserGameActions>.Instance, _state), _state, NullLogger<LaserPacketPipeline>.Instance);

        _state.GameStarted += () => _started++;
        _state.GameEnded += () => _ended++;
        _state.GameStateChanged += () => _changed++;
        _state.PlayerHit += hit => _hits.Add(hit);
    }

    private static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

    [TestMethod]
    public void AWholeGame_StartsScoresAndEnds()
    {
        _pipeline.Process(LaserPacketSource.Live, GameStart);
        Assert.AreEqual(GameStatus.InGame, _state.GetGameStatus());

        _pipeline.Process(LaserPacketSource.Live, Timing);
        Assert.AreEqual(TimeSpan.FromSeconds(600), _state.GetTimeRemaining());

        _pipeline.Process(LaserPacketSource.Live, PlayerScore);
        PlayerScoreDTO? score = _state.GetPlayersScore(1);
        Assert.IsNotNull(score);
        Assert.AreEqual(30, score.Score);
        Assert.AreEqual(10, score.Accuracy);
        Assert.AreEqual(Team.Red, score.Team);

        _pipeline.Process(LaserPacketSource.Live, GameEnd);

        Assert.AreEqual(GameStatus.NotStarted, _state.GetGameStatus());
        Assert.AreEqual(1, _started);
        Assert.AreEqual(1, _ended);
        Assert.HasCount(1, _state.GetAllPlayerScores(), "Scores stay up after the game until the next one starts.");
    }

    [TestMethod]
    public void ARepeatedStatusPacket_DoesNotStartTheGameTwice()
    {
        _pipeline.Process(LaserPacketSource.Live, GameStart);
        _pipeline.Process(LaserPacketSource.Live, GameStart);
        _pipeline.Process(LaserPacketSource.Live, GameEnd);
        _pipeline.Process(LaserPacketSource.Live, GameEnd);

        Assert.AreEqual(1, _started);
        Assert.AreEqual(1, _ended);
    }

    [TestMethod]
    public void ShotConfirmed_RaisesPlayerHit()
    {
        _pipeline.Process(LaserPacketSource.Live, ShotConfirmed);

        Assert.HasCount(1, _hits);
        Assert.AreEqual(11, _hits[0].ShotGunId);
        Assert.AreEqual(1, _hits[0].ShotByGunId);
    }

    [TestMethod]
    public void ShortGarbledAndTeamScorePackets_ChangeNothing()
    {
        _pipeline.Process(LaserPacketSource.Live, Ascii("3,1,0"));          // a score packet with too few fields
        _pipeline.Process(LaserPacketSource.Live, Ascii("x,1,2,3"));        // not a packet type
        _pipeline.Process(LaserPacketSource.Live, Ascii("2,0,120,1"));      // team score, worked out from player scores instead
        _pipeline.Process(LaserPacketSource.Live, ReadOnlySpan<byte>.Empty);
        _pipeline.Dispatch(LaserPacketSource.Live, []);

        Assert.AreEqual(0, _changed);
        Assert.IsEmpty(_state.GetAllPlayerScores());
    }

    [TestMethod]
    public void Dispatch_TakesDecodedFields_LikeProcess()
    {
        _pipeline.Dispatch(LaserPacketSource.Live, ["4", "@015", "0"]);

        Assert.AreEqual(GameStatus.InGame, _state.GetGameStatus());
    }

    [TestMethod]
    public void PacketsFromManyThreads_AreAllApplied()
    {
        // Every packet takes the pipeline's lock, so concurrent callers can't corrupt the score list.
        Parallel.For(1, 201, gun => _pipeline.Process(LaserPacketSource.Live, Ascii($"3,{gun},0,{gun * 10},1,30,3,50,0")));

        List<PlayerScoreDTO> scores = _state.GetAllPlayerScores();

        Assert.HasCount(200, scores);
        Assert.AreEqual(1000, scores.Single(s => s.GunId == 100).Score);
    }

    [TestMethod]
    public void Reset_DropsTheGameWithoutEndingIt()
    {
        _pipeline.Process(LaserPacketSource.Live, GameStart);
        _pipeline.Process(LaserPacketSource.Live, Timing);
        _pipeline.Process(LaserPacketSource.Live, PlayerScore);
        int changedBefore = _changed;

        _state.Reset(LaserPacketSource.Replay);

        Assert.AreEqual(GameStatus.NotStarted, _state.GetGameStatus());
        Assert.AreEqual(TimeSpan.Zero, _state.GetTimeRemaining());
        Assert.IsEmpty(_state.GetAllPlayerScores());
        Assert.AreEqual(LaserPacketSource.Replay, _state.Source);
        Assert.AreEqual(0, _ended, "A reset isn't a finished game, so nothing may record it as one.");
        Assert.AreEqual(changedBefore + 1, _changed);
    }

    [TestMethod]
    public void Source_StartsLive()
    {
        Assert.AreEqual(LaserPacketSource.Live, _state.Source);
    }

    [TestMethod]
    public async Task ARecording_ReplaysToTheSameGameAsTheLivePackets()
    {
        byte[][] game = [GameStart, Timing, PlayerScore, ShotConfirmed, GameEnd];

        foreach (byte[] packet in game)
        {
            _pipeline.Process(LaserPacketSource.Live, packet);
        }

        LaserGameStatusDTO live = _state.GetCurrentStatus();

        using MemoryStream file = new();

        await using (LaserCaptureWriter writer = new(file, leaveOpen: true))
        {
            await writer.WriteHeaderAsync(new LaserCaptureHeader { RecordedAtUtc = DateTime.UtcNow }, CancellationToken.None);

            for (int i = 0; i < game.Length; i++)
            {
                await writer.WritePacketAsync(new LaserCapturePacket(i * 1000L, "192.168.0.10", "192.168.0.255", game[i]), CancellationToken.None);
            }
        }

        file.Position = 0;
        LaserCapture capture = await LaserCaptureReader.ReadAsync(file, CancellationToken.None);

        SetUp();

        foreach (LaserCapturePacket packet in capture.Packets)
        {
            _pipeline.Process(LaserPacketSource.Live, packet.Payload);
        }

        LaserGameStatusDTO replayed = _state.GetCurrentStatus();

        Assert.AreEqual(live.Status, replayed.Status);
        Assert.AreEqual(live.PlayerCount, replayed.PlayerCount);
        Assert.AreEqual(live.PlayerScores.Single().Score, replayed.PlayerScores.Single().Score);
        Assert.AreEqual(1, _started);
        Assert.AreEqual(1, _ended);
    }

    [TestMethod]
    public void SwitchSource_ToReplay_IsRefusedDuringALiveGame()
    {
        _pipeline.Process(LaserPacketSource.Live, GameStart);

        Assert.IsFalse(_pipeline.SwitchSource(LaserPacketSource.Replay));
        Assert.AreEqual(LaserPacketSource.Live, _state.Source);
        Assert.AreEqual(GameStatus.InGame, _state.GetGameStatus(), "A refused switch must leave the real game alone.");
    }

    [TestMethod]
    public void SwitchSource_ToReplay_WorksWhenIdle_AndMarksTheStatusAsAReplay()
    {
        _pipeline.Process(LaserPacketSource.Live, PlayerScore);

        Assert.IsTrue(_pipeline.SwitchSource(LaserPacketSource.Replay));

        LaserGameStatusDTO status = _state.GetCurrentStatus();
        Assert.IsTrue(status.IsReplay);
        Assert.AreEqual(0, status.PlayerCount, "The last live game's scores are dropped.");
    }

    [TestMethod]
    public void SwitchSource_BackToLive_IsAllowedMidReplayGame()
    {
        _pipeline.SwitchSource(LaserPacketSource.Replay);
        _pipeline.Process(LaserPacketSource.Replay, GameStart);

        Assert.IsTrue(_pipeline.SwitchSource(LaserPacketSource.Live));
        Assert.AreEqual(GameStatus.NotStarted, _state.GetGameStatus());
        Assert.IsFalse(_state.GetCurrentStatus().IsReplay);
        Assert.AreEqual(0, _ended, "Abandoning a replayed game isn't a game ending.");
    }

    [TestMethod]
    public void ReplayPackets_AreDropped_WhileFollowingLive()
    {
        _pipeline.Process(LaserPacketSource.Replay, GameStart);

        Assert.AreEqual(GameStatus.NotStarted, _state.GetGameStatus());
        Assert.AreEqual(0, _started);
    }

    [TestMethod]
    public void IdleLivePackets_AreIgnoredAndCounted_DuringAReplay()
    {
        _pipeline.SwitchSource(LaserPacketSource.Replay);
        _pipeline.Process(LaserPacketSource.Replay, GameStart);
        _pipeline.Process(LaserPacketSource.Replay, PlayerScore);

        _pipeline.Process(LaserPacketSource.Live, Ascii("3,7,2,500,1,30,3,90,0"));
        _pipeline.Process(LaserPacketSource.Live, GameEnd);

        Assert.AreEqual(2, _pipeline.IgnoredLivePackets);
        Assert.AreEqual(GameStatus.InGame, _state.GetGameStatus(), "A live game end mustn't end the replayed game.");
        Assert.IsNull(_state.GetPlayersScore(7), "Live scores mustn't leak into the replay.");
        Assert.AreEqual(LaserPacketSource.Replay, _state.Source);
    }

    [TestMethod]
    public void ALiveGameStart_TakesOverFromAReplay()
    {
        int tookOver = 0;
        _pipeline.LiveGameTookOver += () => tookOver++;

        _pipeline.SwitchSource(LaserPacketSource.Replay);
        _pipeline.Process(LaserPacketSource.Replay, GameStart);
        _pipeline.Process(LaserPacketSource.Replay, PlayerScore);
        _started = 0;

        _pipeline.Process(LaserPacketSource.Live, GameStart);

        Assert.AreEqual(1, tookOver);
        Assert.AreEqual(LaserPacketSource.Live, _state.Source);
        Assert.AreEqual(GameStatus.InGame, _state.GetGameStatus());
        Assert.AreEqual(1, _started, "The live game starts from scratch.");
        Assert.IsEmpty(_state.GetAllPlayerScores(), "The replay's scores are gone.");
        Assert.IsFalse(_state.GetCurrentStatus().IsReplay);
        Assert.AreEqual(0, _ended);

        // A replay packet still in flight after the takeover is dropped.
        _pipeline.Process(LaserPacketSource.Replay, GameEnd);
        Assert.AreEqual(GameStatus.InGame, _state.GetGameStatus());
    }

    [TestMethod]
    public void ALiveCountdown_AlsoTakesOverFromAReplay()
    {
        _pipeline.SwitchSource(LaserPacketSource.Replay);

        _pipeline.Process(LaserPacketSource.Live, Timing);

        Assert.AreEqual(LaserPacketSource.Live, _state.Source);
        Assert.AreEqual(TimeSpan.FromSeconds(600), _state.GetTimeRemaining());
    }

    [TestMethod]
    public void StartsAGame_MatchesWhatTheActionsTreatAsAStart()
    {
        Assert.IsTrue(LaserPacketPipeline.StartsAGame(["4", "@015", "0"]));     // InGame
        Assert.IsTrue(LaserPacketPipeline.StartsAGame(["4", "@016", "0"]));     // GetReady
        Assert.IsTrue(LaserPacketPipeline.StartsAGame(["1", "7573", "\"@004\"", "600", "1"]));

        Assert.IsFalse(LaserPacketPipeline.StartsAGame(["4", "@014", "0"]));    // NotStarted
        Assert.IsFalse(LaserPacketPipeline.StartsAGame(["1", "7573", "\"@004\"", "0", "1"]));
        Assert.IsFalse(LaserPacketPipeline.StartsAGame(["4", "@01615@01700@0012@0003"])); // a settings packet
        Assert.IsFalse(LaserPacketPipeline.StartsAGame(["3", "1", "0", "30", "1", "30", "3", "10", "0"]));
        Assert.IsFalse(LaserPacketPipeline.StartsAGame(["1", "7573"]));
        Assert.IsFalse(LaserPacketPipeline.StartsAGame([]));
    }
}
