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
        _pipeline = new LaserPacketPipeline(new LaserGameActions(NullLogger<LaserGameActions>.Instance, _state), NullLogger<LaserPacketPipeline>.Instance);

        _state.GameStarted += () => _started++;
        _state.GameEnded += () => _ended++;
        _state.GameStateChanged += () => _changed++;
        _state.PlayerHit += hit => _hits.Add(hit);
    }

    private static byte[] Ascii(string text) => Encoding.Latin1.GetBytes(text);

    [TestMethod]
    public void AWholeGame_StartsScoresAndEnds()
    {
        _pipeline.Process(GameStart);
        Assert.AreEqual(GameStatus.InGame, _state.GetGameStatus());

        _pipeline.Process(Timing);
        Assert.AreEqual(TimeSpan.FromSeconds(600), _state.GetTimeRemaining());

        _pipeline.Process(PlayerScore);
        PlayerScoreDTO? score = _state.GetPlayersScore(1);
        Assert.IsNotNull(score);
        Assert.AreEqual(30, score.Score);
        Assert.AreEqual(10, score.Accuracy);
        Assert.AreEqual(Team.Red, score.Team);

        _pipeline.Process(GameEnd);

        Assert.AreEqual(GameStatus.NotStarted, _state.GetGameStatus());
        Assert.AreEqual(1, _started);
        Assert.AreEqual(1, _ended);
        Assert.HasCount(1, _state.GetAllPlayerScores(), "Scores stay up after the game until the next one starts.");
    }

    [TestMethod]
    public void ARepeatedStatusPacket_DoesNotStartTheGameTwice()
    {
        _pipeline.Process(GameStart);
        _pipeline.Process(GameStart);
        _pipeline.Process(GameEnd);
        _pipeline.Process(GameEnd);

        Assert.AreEqual(1, _started);
        Assert.AreEqual(1, _ended);
    }

    [TestMethod]
    public void ShotConfirmed_RaisesPlayerHit()
    {
        _pipeline.Process(ShotConfirmed);

        Assert.HasCount(1, _hits);
        Assert.AreEqual(11, _hits[0].ShotGunId);
        Assert.AreEqual(1, _hits[0].ShotByGunId);
    }

    [TestMethod]
    public void ShortGarbledAndTeamScorePackets_ChangeNothing()
    {
        _pipeline.Process(Ascii("3,1,0"));          // a score packet with too few fields
        _pipeline.Process(Ascii("x,1,2,3"));        // not a packet type
        _pipeline.Process(Ascii("2,0,120,1"));      // team score, worked out from player scores instead
        _pipeline.Process(ReadOnlySpan<byte>.Empty);
        _pipeline.Dispatch([]);

        Assert.AreEqual(0, _changed);
        Assert.IsEmpty(_state.GetAllPlayerScores());
    }

    [TestMethod]
    public void Dispatch_TakesDecodedFields_LikeProcess()
    {
        _pipeline.Dispatch(["4", "@015", "0"]);

        Assert.AreEqual(GameStatus.InGame, _state.GetGameStatus());
    }

    [TestMethod]
    public void PacketsFromManyThreads_AreAllApplied()
    {
        // Every packet takes the pipeline's lock, so concurrent callers can't corrupt the score list.
        Parallel.For(1, 201, gun => _pipeline.Process(Ascii($"3,{gun},0,{gun * 10},1,30,3,50,0")));

        List<PlayerScoreDTO> scores = _state.GetAllPlayerScores();

        Assert.HasCount(200, scores);
        Assert.AreEqual(1000, scores.Single(s => s.GunId == 100).Score);
    }

    [TestMethod]
    public void Reset_DropsTheGameWithoutEndingIt()
    {
        _pipeline.Process(GameStart);
        _pipeline.Process(Timing);
        _pipeline.Process(PlayerScore);
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
            _pipeline.Process(packet);
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
            _pipeline.Process(packet.Payload);
        }

        LaserGameStatusDTO replayed = _state.GetCurrentStatus();

        Assert.AreEqual(live.Status, replayed.Status);
        Assert.AreEqual(live.PlayerCount, replayed.PlayerCount);
        Assert.AreEqual(live.PlayerScores.Single().Score, replayed.PlayerScores.Single().Score);
        Assert.AreEqual(1, _started);
        Assert.AreEqual(1, _ended);
    }
}
