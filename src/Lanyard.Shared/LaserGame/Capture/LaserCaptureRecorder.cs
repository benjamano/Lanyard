using System.Net;
using System.Threading.Channels;
using Lanyard.Shared.DTO;

namespace Lanyard.Shared.LaserGame.Capture;

public enum LaserCaptureStopReason
{
    /// <summary>Someone pressed Stop.</summary>
    Stopped = 0,

    /// <summary>"Record next game" saw the game end.</summary>
    GameFinished = 1,

    MaxDuration = 2,
    MaxSize = 3,

    /// <summary>The file couldn't be written.</summary>
    Failed = 4,
}

public sealed record LaserCaptureRecordingOptions
{
    public LaserCaptureStopMode Mode { get; init; } = LaserCaptureStopMode.NextGame;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(PacketCaptureRules.DefaultMaxMinutes);
    public long MaxBytes { get; init; } = PacketCaptureRules.MaxRecordingBytes;
    public TimeSpan NextGameGracePeriod { get; init; } = PacketCaptureRules.NextGameGracePeriod;

    /// <summary>Packets the writer can fall behind by before new ones are dropped.</summary>
    public int ChannelCapacity { get; init; } = 10_000;
}

public sealed record LaserCaptureRecordingResult(LaserCaptureStopReason Reason, long Packets, long Dropped, long DurationMs, int Games, string? Error);

/// <summary>
/// Records one laser game recording to a stream. <see cref="TryRecord"/> is called on the packet
/// capture thread, so it only copies the payload into a bounded queue and never blocks or does IO;
/// if the writer falls that far behind, the packet is counted as dropped instead. A single writer
/// task turns the queue into <see cref="LaserCaptureFormat"/> lines, flushing after each batch so a
/// crash loses almost nothing, and writes the footer when recording stops.
/// </summary>
public sealed class LaserCaptureRecorder : IAsyncDisposable
{
    private const int NotStopped = -1;

    private readonly Stream _output;
    private readonly LaserCaptureHeader _header;
    private readonly LaserCaptureRecordingOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Channel<Entry> _queue;
    private readonly CancellationTokenSource _abort = new();
    private readonly TaskCompletionSource<LaserCaptureRecordingResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _stopReason = NotStopped;
    private int _started;
    private long _recorded;
    private long _dropped;
    private int _games;
    private int _gameInProgress;

    /// <param name="delay">How to wait for the time limits; tests pass one they control.</param>
    public LaserCaptureRecorder(Stream output, LaserCaptureHeader header, LaserCaptureRecordingOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _output = output;
        _header = header;
        _options = options;
        _delay = delay ?? ((wait, token) => Task.Delay(wait, token));

        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            // Normally only the capture thread writes, but reopening the capture card starts a new one.
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public DateTime StartedUtc { get; private set; }
    public long Recorded => Interlocked.Read(ref _recorded);
    public long Dropped => Interlocked.Read(ref _dropped);
    public int Games => Volatile.Read(ref _games);
    public bool IsStopping => Volatile.Read(ref _stopReason) != NotStopped;

    /// <summary>Finishes when the footer is written (or writing failed). Never faults.</summary>
    public Task<LaserCaptureRecordingResult> Completion => _completion.Task;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The recorder has already started.");
        }

        StartedUtc = DateTime.UtcNow;

        _ = Task.Run(RunAsync);
        _ = Task.Run(StopAfterMaxDurationAsync);
    }

    /// <summary>
    /// Queues one game packet. Safe on the capture thread: no IO, no locks, one small copy.
    /// False when recording has stopped or the writer is too far behind.
    /// </summary>
    public bool TryRecord(DateTime capturedUtc, IPAddress? source, IPAddress? destination, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || IsStopping)
        {
            return false;
        }

        if (_queue.Writer.TryWrite(new Entry(capturedUtc.Ticks, source, destination, payload.ToArray())))
        {
            return true;
        }

        if (!IsStopping)
        {
            Interlocked.Increment(ref _dropped);
        }

        return false;
    }

    /// <summary>Tell the recorder a live game started, so it can count games.</summary>
    public void OnGameStarted() => Interlocked.Exchange(ref _gameInProgress, 1);

    /// <summary>Tell the recorder a live game ended. "Record next game" stops shortly after.</summary>
    public void OnGameEnded()
    {
        // A game already running when recording started isn't a whole game, so it doesn't count.
        if (Interlocked.Exchange(ref _gameInProgress, 0) != 1)
        {
            return;
        }

        Interlocked.Increment(ref _games);

        if (_options.Mode == LaserCaptureStopMode.NextGame)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // Keeps the final score packets the control box sends just after the end.
                    await _delay(_options.NextGameGracePeriod, _abort.Token);
                    RequestStop(LaserCaptureStopReason.GameFinished);
                }
                catch (OperationCanceledException)
                {
                }
            });
        }
    }

    public Task<LaserCaptureRecordingResult> StopAsync()
    {
        RequestStop(LaserCaptureStopReason.Stopped);

        return Completion;
    }

    private void RequestStop(LaserCaptureStopReason reason)
    {
        if (Interlocked.CompareExchange(ref _stopReason, (int)reason, NotStopped) == NotStopped)
        {
            _queue.Writer.TryComplete();
        }
    }

    private async Task StopAfterMaxDurationAsync()
    {
        try
        {
            await _delay(_options.MaxDuration, _abort.Token);
            RequestStop(LaserCaptureStopReason.MaxDuration);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync()
    {
        CancellationToken token = _abort.Token;
        long? firstTicks = null;
        long lastOffsetMs = 0;
        string? error = null;

        await using LaserCaptureWriter writer = new(_output);

        try
        {
            await writer.WriteHeaderAsync(_header, token);

            while (await _queue.Reader.WaitToReadAsync(token))
            {
                while (_queue.Reader.TryRead(out Entry entry))
                {
                    firstTicks ??= entry.CapturedTicks;

                    // Capture timestamps can step back a hair between interrupts; never write time going backwards.
                    long offsetMs = Math.Max(lastOffsetMs, (entry.CapturedTicks - firstTicks.Value) / TimeSpan.TicksPerMillisecond);

                    await writer.WritePacketAsync(new LaserCapturePacket(offsetMs, entry.Source?.ToString(), entry.Destination?.ToString(), entry.Payload), token);

                    lastOffsetMs = offsetMs;
                    Interlocked.Increment(ref _recorded);

                    if (writer.BytesWritten >= _options.MaxBytes)
                    {
                        RequestStop(LaserCaptureStopReason.MaxSize);
                        break;
                    }
                }

                await writer.FlushAsync(token);

                if (Volatile.Read(ref _stopReason) == (int)LaserCaptureStopReason.MaxSize)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            error = ex.Message;
            RequestStop(LaserCaptureStopReason.Failed);
            Volatile.Write(ref _stopReason, (int)LaserCaptureStopReason.Failed);
        }
        catch (OperationCanceledException)
        {
            // Disposed while recording: leave the file without a footer, as a crash would.
        }

        LaserCaptureStopReason reason = (LaserCaptureStopReason)Volatile.Read(ref _stopReason);

        if (error == null && !token.IsCancellationRequested)
        {
            try
            {
                await writer.WriteFooterAsync(new LaserCaptureFooter
                {
                    Packets = Recorded,
                    Dropped = Dropped,
                    DurationMs = lastOffsetMs,
                    Games = Games,
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                reason = LaserCaptureStopReason.Failed;
            }
        }

        // Stops the max-duration and grace-period waits.
        _abort.Cancel();

        _completion.TrySetResult(new LaserCaptureRecordingResult(reason, Recorded, Dropped, lastOffsetMs, Games, error));
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _started) == 0)
        {
            await _output.DisposeAsync();
            _abort.Dispose();
            return;
        }

        RequestStop(LaserCaptureStopReason.Stopped);
        await Completion;
        _abort.Dispose();
    }

    private readonly record struct Entry(long CapturedTicks, IPAddress? Source, IPAddress? Destination, byte[] Payload);
}
