using Lanyard.Shared.Enum;
using Lanyard.Shared.LaserGame;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.LaserGame;

[TestClass]
public class LaserGameModesTests
{
    [TestMethod]
    public void TryParse_ReadsTheModeFieldOfARealTimingPacket()
    {
        // Same capture as LaserPacketDecoderTests.Decode_TimingPacket.
        string[] fields = LaserPacketDecoder.Decode(Convert.FromHexString("312c373537332c224030303422" + "2c3630302c31"));

        Assert.IsTrue(LaserGameModes.TryParse(fields[2], out GameMode mode));
        Assert.AreEqual(GameMode.FastTeam, mode);
    }

    [DataTestMethod]
    [DataRow("\"@001\"", GameMode.StandardSolo)]
    [DataRow("@002", GameMode.StandardTeam)]
    [DataRow("@014", GameMode.ContinuousSolo)]
    [DataRow("009", GameMode.BirthdaySolo)]
    [DataRow(" 018 ", GameMode.Zombie)]
    public void TryParse_AcceptsTimingAndSettingsShapes(string raw, GameMode expected)
    {
        Assert.IsTrue(LaserGameModes.TryParse(raw, out GameMode mode));
        Assert.AreEqual(expected, mode);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("\"@\"")]
    [DataRow("@abc")]
    [DataRow("@000")]
    [DataRow("@019")]
    [DataRow("-1")]
    public void TryParse_RejectsUnknownOrGarbledModes(string? raw)
    {
        Assert.IsFalse(LaserGameModes.TryParse(raw, out _));
    }

    [TestMethod]
    public void IsSolo_OnlyForTheSoloModes()
    {
        GameMode[] solo = [GameMode.StandardSolo, GameMode.FastSolo, GameMode.SoloElimination, GameMode.BirthdaySolo, GameMode.ContinuousSolo];

        foreach (GameMode mode in System.Enum.GetValues<GameMode>())
        {
            Assert.AreEqual(solo.Contains(mode), mode.IsSolo(), mode.ToString());
        }
    }
}
