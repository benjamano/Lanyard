using Lanyard.Shared.LaserGame;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.LaserGame;

[TestClass]
public class LaserPacketDecoderTests
{
    // Hex as captured from a real Begeara 2 control box.
    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    [TestMethod]
    public void Decode_TimingPacket()
    {
        string[] fields = LaserPacketDecoder.Decode(Hex("312c373537332c224030303422" + "2c3630302c31"));

        CollectionAssert.AreEqual(new[] { "1", "7573", "\"@004\"", "600", "1" }, fields);
    }

    [TestMethod]
    public void Decode_PlayerScorePacket()
    {
        string[] fields = LaserPacketDecoder.Decode(Hex("332c312c302c33302c312c33302c332c31302c30"));

        CollectionAssert.AreEqual(new[] { "3", "1", "0", "30", "1", "30", "3", "10", "0" }, fields);
    }

    [TestMethod]
    public void Decode_GameStartAndEndPackets()
    {
        CollectionAssert.AreEqual(new[] { "4", "@015", "0" }, LaserPacketDecoder.Decode(Hex("342c403031352c30")));
        CollectionAssert.AreEqual(new[] { "4", "@014", "0" }, LaserPacketDecoder.Decode(Hex("342c403031342c30")));
    }

    [TestMethod]
    public void Decode_ShotConfirmedPacket()
    {
        string[] fields = LaserPacketDecoder.Decode(Hex("352c31312c312c322c302c302c302c30"));

        CollectionAssert.AreEqual(new[] { "5", "11", "1", "2", "0", "0", "0", "0" }, fields);
    }

    [TestMethod]
    public void Decode_MatchesTheOldHexStringRoundTrip_ForEveryByteValue()
    {
        byte[] payload = Enumerable.Range(0, 256).Select(b => (byte)b).ToArray();

        // What PacketSniffer did before: bytes -> "0A1B..." -> one char per byte -> split.
        string hex = BitConverter.ToString(payload).Replace("-", "");
        string old = string.Concat(Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToChar(Convert.ToByte(hex.Substring(i * 2, 2), 16))));

        CollectionAssert.AreEqual(old.Split(","), LaserPacketDecoder.Decode(payload));
    }

    [TestMethod]
    public void Decode_EmptyPayload_GivesNoFields()
    {
        Assert.AreEqual(0, LaserPacketDecoder.Decode([]).Length);
    }

    [TestMethod]
    public void Decode_KeepsEmptyTrailingField()
    {
        CollectionAssert.AreEqual(new[] { "4", "@015", "" }, LaserPacketDecoder.Decode("4,@015,"u8));
    }

    [TestMethod]
    public void MinimumFieldCount_CoversTheFieldsEachHandlerReads()
    {
        // Timing reads [3], player score reads [7], status reads [1], shot reads [2].
        Assert.AreEqual(4, LaserPacketDecoder.MinimumFieldCount(LaserPacketDecoder.TimingPacket));
        Assert.AreEqual(8, LaserPacketDecoder.MinimumFieldCount(LaserPacketDecoder.PlayerScorePacket));
        Assert.AreEqual(2, LaserPacketDecoder.MinimumFieldCount(LaserPacketDecoder.GameStatusPacket));
        Assert.AreEqual(3, LaserPacketDecoder.MinimumFieldCount(LaserPacketDecoder.ShotConfirmedPacket));
        Assert.AreEqual(1, LaserPacketDecoder.MinimumFieldCount(99));
    }
}
