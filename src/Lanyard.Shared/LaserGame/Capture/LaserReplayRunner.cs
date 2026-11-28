using Lanyard.Shared.LaserGame.Pipeline;

namespace Lanyard.Shared.LaserGame.Capture;

public enum LaserReplayEnd
{
    /// <summary>Every packet was played.</summary>
    Completed = 0,

    /// <summary>Someone stopped it.</summary>
    Stopped = 1,

    /// <summary>A real game started on the arena, which always wins.</summary>
    TakenOver = 2,
}

public sealed record LaserReplayOutcome(LaserReplayEnd End, int PacketsPlayed, int TotalPackets);

public readonly record struct LaserReplayProgress(int PacketsPlayed, int TotalPackets)
{
    public int Percent => TotalPackets == 0 ? 100 : (int)(PacketsPlayed * 100L / TotalPackets);
}

/// <summary>
/// Plays a recording back into the kiosk's real game pipeline (decoder, actions, game state and
/// status publisher), keeping the recorded gaps between packets divided by the speed. Call
/// <see cref="TryBegin"/> first: it switches the game state over to the replay, and refuses while
/// a real game is in progress. A live game starting mid-replay takes over and ends the replay.
/// </summary>
public sealed class LaserReplayRunner(ILaserPacketPipeline pipeline, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>Plays every packet straight away, for tests.</summary>
    public const double Instant = 0;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((wait, token) => Task.Delay(wait, token));

    private int _begun;

    /// <summary>
    /// Points the game state at the replay, dropping whatever was on screen. False while a live
    /// game is in progress, in which case nothing changes.
    /// </summary>
    public bool TryBegin()
    {
        if (!pipeline.SwitchSource(LaserPacketSource.Replay))
        {
            return false;
        }

        Interlocked.Exchange(ref _begun, 1);

        return true;
    }

    public async Task<LaserReplayOutcome> PlayAsync(LaserCapture capture, double speed, IProgress<LaserReplayProgress>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _begun, 0) != 1)
        {
            throw new InvalidOperationException("Call TryBegin before playing.");
        }

        if (speed < 0 || double.IsNaN(speed) || double.IsInfinity(speed))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "Speed must be zero (instant) or more.");
        }

        using CancellationTokenSource playing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool takenOver = false;

        void OnTakeover()
        {
            takenOver = true;
            playing.Cancel();
        }

        pipeline.LiveGameTookOver += OnTakeover;

        int played = 0;
        int total = capture.Packets.Count;

        try
        {
            long previousOffsetMs = capture.Packets.Count > 0 ? capture.Packets[0].OffsetMs : 0;

            foreach (LaserCapturePacket packet in capture.Packets)
            {
                long gapMs = packet.OffsetMs - previousOffsetMs;
                previousOffsetMs = packet.OffsetMs;

                if (speed > 0 && gapMs > 0)
                {
                    await _delay(TimeSpan.FromMilliseconds(gapMs / speed), playing.Token);
                }

                playing.Token.ThrowIfCancellationRequested();

                pipeline.Process(LaserPacketSource.Replay, packet.Payload);
                played++;

                progress?.Report(new LaserReplayProgress(played, total));
            }
        }
        catch (OperationCanceledException) when (playing.IsCancellationRequested)
        {
            return new LaserReplayOutcome(takenOver ? LaserReplayEnd.TakenOver : LaserReplayEnd.Stopped, played, total);
        }
        finally
        {
            pipeline.LiveGameTookOver -= OnTakeover;
        }

        return new LaserReplayOutcome(takenOver ? LaserReplayEnd.TakenOver : LaserReplayEnd.Completed, played, total);
    }
}
