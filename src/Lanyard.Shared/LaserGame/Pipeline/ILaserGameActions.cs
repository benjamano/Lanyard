namespace Lanyard.Shared.LaserGame.Pipeline;

/// <summary>Turns each kind of decoded control box packet into changes to the game state.</summary>
public interface ILaserGameActions
{
    void HandleTimingPacket(string[] packetData);
    void HandlePlayerScorePacket(string[] packetData);
    void HandleGameStatusPacket(string[] packetData);
    void HandleShotConfirmedPacket(string[] packetData);
}
