public interface IActionFunctions
{
    Task HandleTimingPacketAsync(string[] packetData);
    Task HandlePlayerScorePacketAsync(string[] packetData);
    Task HandleGameStatusPacketAsync(string[] packetData);
}