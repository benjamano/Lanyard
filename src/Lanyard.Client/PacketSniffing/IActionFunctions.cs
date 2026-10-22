public interface IActionFunctions
{
    void HandleTimingPacket(string[] packetData);
    void HandlePlayerScorePacket(string[] packetData);
    void HandleGameStatusPacket(string[] packetData);
    void HandleShotConfirmedPacket(string[] packetData);
}