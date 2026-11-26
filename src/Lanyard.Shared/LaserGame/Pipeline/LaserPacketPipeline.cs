using Lanyard.Shared.Enum;
using Microsoft.Extensions.Logging;

namespace Lanyard.Shared.LaserGame.Pipeline;

public interface ILaserPacketPipeline
{
    /// <summary>
    /// Decodes one control box UDP payload and applies it to the game state, if it comes from the
    /// source the game state is following. Packets are handled one at a time, in the order this is
    /// called, whichever thread calls it.
    /// </summary>
    void Process(LaserPacketSource source, ReadOnlySpan<byte> payload);

    /// <summary>Applies one already-decoded packet. Same rules as <see cref="Process"/>.</summary>
    void Dispatch(LaserPacketSource source, string[] decodedData);

    /// <summary>
    /// Makes the game state follow a different source, dropping whatever game it had. Refused
    /// (returns false) when switching away from a live game in progress: a replay must never
    /// interrupt a real game. Switching back to live is always allowed.
    /// </summary>
    bool SwitchSource(LaserPacketSource source);

    /// <summary>Live packets ignored because a replay was running.</summary>
    long IgnoredLivePackets { get; }

    /// <summary>
    /// Raised (outside the pipeline's lock) when a live game started while a replay was running.
    /// The pipeline has already switched back to live; whoever runs the replay should stop it.
    /// </summary>
    event Action? LiveGameTookOver;
}

/// <summary>
/// The one way a game packet reaches the game state, whether it came off the network or out of a
/// recording. Packets must be applied strictly in order: a game start or end handled out of turn
/// publishes the wrong status edge, and the server records games off those edges. So every packet,
/// and every switch between live and replay, takes <see cref="_gate"/>. That is also what stops a
/// live packet and a replayed one ever interleaving.
/// </summary>
public sealed class LaserPacketPipeline(ILaserGameActions actions, IGameStateService gameState, ILogger<LaserPacketPipeline> logger) : ILaserPacketPipeline
{
    private readonly object _gate = new();

    private long _ignoredLivePackets;

    public event Action? LiveGameTookOver;

    public long IgnoredLivePackets => Interlocked.Read(ref _ignoredLivePackets);

    public void Process(LaserPacketSource source, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return;
        }

        Dispatch(source, LaserPacketDecoder.Decode(payload));
    }

    public void Dispatch(LaserPacketSource source, string[] decodedData)
    {
        bool tookOver = false;

        lock (_gate)
        {
            LaserPacketSource following = gameState.Source;

            if (source != following)
            {
                if (source == LaserPacketSource.Replay)
                {
                    // A replay that was taken over may still have a packet in flight.
                    return;
                }

                if (!StartsAGame(decodedData))
                {
                    // Idle chatter from the control box while a replay runs.
                    Interlocked.Increment(ref _ignoredLivePackets);
                    return;
                }

                logger.LogInformation("A live game started during a replay; stopping the replay and following the live game.");

                gameState.Reset(LaserPacketSource.Live);
                tookOver = true;
            }

            DispatchLocked(decodedData);
        }

        if (tookOver)
        {
            LiveGameTookOver?.Invoke();
        }
    }

    public bool SwitchSource(LaserPacketSource source)
    {
        lock (_gate)
        {
            LaserPacketSource following = gameState.Source;

            if (following == LaserPacketSource.Live && source != LaserPacketSource.Live && gameState.GetGameStatus() != GameStatus.NotStarted)
            {
                return false;
            }

            gameState.Reset(source);
            Interlocked.Exchange(ref _ignoredLivePackets, 0);

            return true;
        }
    }

    /// <summary>The packets LaserGameActions treats as a game starting: a countdown, or an InGame/GetReady status.</summary>
    public static bool StartsAGame(string[] decodedData)
    {
        if (decodedData.Length == 0 || !int.TryParse(decodedData[0], out int packetType)
            || decodedData.Length < LaserPacketDecoder.MinimumFieldCount(packetType))
        {
            return false;
        }

        return packetType switch
        {
            LaserPacketDecoder.TimingPacket => int.TryParse(decodedData[3], out int secondsLeft) && secondsLeft > 0,
            LaserPacketDecoder.GameStatusPacket => int.TryParse(decodedData[1].Replace("@", ""), out int status)
                && (status == (int)GameStatus.InGame || status == (int)GameStatus.GetReady),
            _ => false,
        };
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
