using System.Text;

namespace Lanyard.Shared.LaserGame;

/// <summary>
/// Turns a laser game control box's UDP payload into its comma-separated fields, e.g.
/// <c>4,@015,0</c> becomes <c>["4", "@015", "0"]</c>. The box sends plain ASCII, and each byte
/// maps to the char of the same value (Latin-1), which is what the old hex-string round trip did.
/// </summary>
public static class LaserPacketDecoder
{
    public const int TimingPacket = 1;
    public const int TeamScorePacket = 2;
    public const int PlayerScorePacket = 3;
    public const int GameStatusPacket = 4;
    public const int ShotConfirmedPacket = 5;

    public static string[] Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        return Encoding.Latin1.GetString(payload).Split(',');
    }

    /// <summary>The fields a handler reads, so a short or garbled packet can be dropped before it is indexed.</summary>
    public static int MinimumFieldCount(int packetType) => packetType switch
    {
        // type, game number, mode, seconds remaining, ?
        TimingPacket => 4,
        // type, gun, team, score, rank, ?, shots fired, accuracy, ?
        PlayerScorePacket => 8,
        // type, status, ?
        GameStatusPacket => 2,
        // type, gun hit, shooter gun, ...
        ShotConfirmedPacket => 3,
        _ => 1,
    };
}
