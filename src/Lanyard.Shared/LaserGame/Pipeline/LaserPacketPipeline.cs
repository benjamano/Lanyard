using Microsoft.Extensions.Logging;

namespace Lanyard.Shared.LaserGame.Pipeline;

public interface ILaserPacketPipeline
{
    /// <summary>
    /// Decodes one control box UDP payload and applies it to the game state. Packets are handled
    /// one at a time, in the order this is called, whichever thread calls it.
    /// </summary>
    void Process(ReadOnlySpan<byte> payload);

    /// <summary>Applies one already-decoded packet. Same ordering guarantee as <see cref="Process"/>.</summary>
    void Dispatch(string[] decodedData);
}

/// <summary>
/// The one way a game packet reaches the game state, whether it came off the network or out of a
/// recording. Packets must be applied strictly in order: a game start or end handled out of turn
/// publishes the wrong status edge, and the server records games off those edges. So every packet
/// takes <see cref="_gate"/>, which also stops a live packet and a replayed one ever interleaving.
/// </summary>
public sealed class LaserPacketPipeline(ILaserGameActions actions, ILogger<LaserPacketPipeline> logger) : ILaserPacketPipeline
{
    private readonly object _gate = new();

    public void Process(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return;
        }

        Dispatch(LaserPacketDecoder.Decode(payload));
    }

    public void Dispatch(string[] decodedData)
    {
        lock (_gate)
        {
            DispatchLocked(decodedData);
        }
    }

    private void DispatchLocked(string[] decodedData)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("Game packet: {Payload}", string.Join(',', decodedData));
        }

        if (decodedData.Length == 0 || !int.TryParse(decodedData[0], out int packetType))
        {
            logger.LogDebug("Invalid number for packet type received: {Value}", decodedData.FirstOrDefault());
            return;
        }

        if (decodedData.Length < LaserPacketDecoder.MinimumFieldCount(packetType))
        {
            logger.LogDebug("Dropped a type {PacketType} packet with only {FieldCount} fields.", packetType, decodedData.Length);
            return;
        }

        switch (packetType)
        {
            case LaserPacketDecoder.TimingPacket:
                actions.HandleTimingPacket(decodedData);
                break;
            case LaserPacketDecoder.TeamScorePacket:
                // Not needed: team scores can be worked out from the player scores.
                break;
            case LaserPacketDecoder.PlayerScorePacket:
                actions.HandlePlayerScorePacket(decodedData);
                break;
            case LaserPacketDecoder.GameStatusPacket:
                actions.HandleGameStatusPacket(decodedData);
                break;
            case LaserPacketDecoder.ShotConfirmedPacket:
                actions.HandleShotConfirmedPacket(decodedData);
                break;
        }
    }
}
