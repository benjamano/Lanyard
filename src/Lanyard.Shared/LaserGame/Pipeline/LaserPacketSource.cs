namespace Lanyard.Shared.LaserGame.Pipeline;

/// <summary>Where a game packet came from.</summary>
public enum LaserPacketSource
{
    /// <summary>Captured off the network from the arena's control box.</summary>
    Live = 0,

    /// <summary>Read back from a recording.</summary>
    Replay = 1,
}
