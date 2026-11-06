using System.Diagnostics;

namespace Lanyard.Shared.Dmx;

/// <summary>
/// One physical DMX interface, as the frame loop sees it. The kiosk's FTDI adapter implements it;
/// tests use a fake. Every method returns null on success or a short description of the failure,
/// and none may throw.
/// </summary>
public interface IDmxPort
{
    string? Open(uint deviceIndex);

    /// <summary>Break then mark-after-break: the line signal that starts a DMX512 frame.</summary>
    string? SendBreak();

    /// <summary>Writes the start code and 512 channel bytes.</summary>
    string? Write(byte[] frame);

    void Close();
}

/// <summary>
/// The kiosk's DMX output: a 512-channel frame buffer plus one dedicated thread that sends it about
/// 40 times a second. Lives in Shared (not the WPF client) so it can be unit tested.
/// </summary>
/// <remarks>
/// It replaces a thread-pool timer that sent frames while holding the same lock incoming SignalR
/// batches needed, with two Thread.Sleep(1) calls inside it. At Windows' default 15.6 ms timer
/// resolution a frame took longer than the 25 ms period, so callbacks piled up on the lock and
/// incoming values queued behind USB writes. Now writers only take <see cref="_frameLock"/> long
/// enough to copy bytes, and all USB I/O happens on the frame thread outside it.
///
/// <see cref="Open"/> is idempotent: the server re-sends DMX settings on every SignalR reconnect,
/// and re-opening an FTDI handle that's already open used to fail and leave output dead.
/// After <see cref="FailuresBeforeReopen"/> failed frames in a row (a USB unplug) the port is
/// closed and re-opened every <see cref="ReopenInterval"/> until it comes back.
/// </remarks>
public sealed class DmxFrameOutput : IDisposable
{
    public const int ChannelCount = 512;

    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(25);
    public static readonly TimeSpan ReopenInterval = TimeSpan.FromSeconds(2);
    public const int FailuresBeforeReopen = 10;

    private readonly Func<IDmxPort> _createPort;
    private readonly Action<string> _warn;
    private readonly TimeProvider _time;

    // Slot 0 is the DMX null start code; channels index directly.
    private readonly byte[] _frame = new byte[ChannelCount + 1];
    private readonly object _frameLock = new();

    private bool _flashing;
    private readonly Dictionary<int, byte> _pendingDuringFlash = [];

    // Serialises Open/Close/Dispose. Held while joining the frame thread, which never takes it.
    private readonly object _lifecycleLock = new();

    // Port state, shared with the frame thread; only ever held briefly.
    private readonly object _stateLock = new();
    private IDmxPort? _port;
    private uint? _wantedIndex;
    private bool _isOpen;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private bool _disposed;

    private long _framesWritten;
    private volatile string? _lastWriteError;
    private DateTime? _lastNonZeroFrameUtc;
    private int _consecutiveFailures;
    private DateTime _nextReopenUtc;

    private readonly bool _runFrameThread;

    /// <param name="runFrameThread">
    /// False for tests: no thread is started and frames go out only when <see cref="SendFrameNow"/> is called.
    /// </param>
    public DmxFrameOutput(Func<IDmxPort> createPort, Action<string>? warn = null, TimeProvider? time = null, bool runFrameThread = true)
    {
        _createPort = createPort;
        _warn = warn ?? (_ => { });
        _time = time ?? TimeProvider.System;
        _runFrameThread = runFrameThread;
        _frame[0] = 0x00;
    }

    public bool IsOpen
    {
        get
        {
            lock (_stateLock)
            {
                return _isOpen;
            }
        }
    }

    /// <summary>The USB index the output is driving (or trying to reopen), or null.</summary>
    public uint? DeviceIndex
    {
        get
        {
            lock (_stateLock)
            {
                return _wantedIndex;
            }
        }
    }

    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    /// <summary>The failure from the most recent frame or open attempt, or null when it went out whole.</summary>
    public string? LastWriteError => _lastWriteError;

    /// <summary>When a frame with any channel above zero last went out; null if none has since opening.</summary>
    public DateTime? LastNonZeroFrameUtc
    {
        get
        {
            lock (_frameLock)
            {
                return _lastNonZeroFrameUtc;
            }
        }
    }

    /// <summary>
    /// Starts driving the interface at <paramref name="deviceIndex"/>. Already driving that index:
    /// nothing happens. A different index: the old one is closed first. False when the port
    /// wouldn't open; the output then keeps retrying in the background.
    /// </summary>
    public bool Open(uint deviceIndex)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            lock (_stateLock)
            {
                if (_wantedIndex == deviceIndex)
                {
                    return _isOpen;
                }
            }

            StopThreadAndClosePort(blackout: false);

            bool opened;

            lock (_stateLock)
            {
                _wantedIndex = deviceIndex;
                opened = TryOpenPort(deviceIndex);
            }

            if (_runFrameThread)
            {
                _stopRequested = false;
                _thread = new Thread(RunFrameLoop) { IsBackground = true, Name = "DMX frame output", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }

            return opened;
        }
    }

    // Caller holds _stateLock.
    private bool TryOpenPort(uint deviceIndex)
    {
        IDmxPort port;

        try
        {
            port = _createPort();
        }
        catch (Exception ex)
        {
            _lastWriteError = "driver unavailable: " + ex.Message;
            _warn($"DMX driver unavailable: {ex.Message}");
            _nextReopenUtc = _time.GetUtcNow().UtcDateTime + ReopenInterval;
            return false;
        }

        string? error = port.Open(deviceIndex);

        if (error != null)
        {
            port.Close();
            _lastWriteError = "open failed: " + error;
            _warn($"Couldn't open DMX device {deviceIndex}: {error}. Retrying every {ReopenInterval.TotalSeconds:0} s.");
            _nextReopenUtc = _time.GetUtcNow().UtcDateTime + ReopenInterval;
            return false;
        }

        _port = port;
        _isOpen = true;
        _consecutiveFailures = 0;
        _lastWriteError = null;

        return true;
    }

    /// <summary>Sets a channel (1-512). Anything else is ignored rather than aborting the batch it came in.</summary>
    public void SetChannel(int channel, byte value)
    {
        if (channel is < 1 or > ChannelCount)
        {
            return;
        }

        lock (_frameLock)
        {
            if (_flashing)
            {
                _pendingDuringFlash[channel] = value;
            }
            else
            {
                _frame[channel] = value;
            }
        }
    }

    /// <summary>Applies many channels under one lock. Returns how many were outside 1-512 and skipped.</summary>
    public int SetChannels(IEnumerable<(int Channel, byte Value)> channels)
    {
        int skipped = 0;

        lock (_frameLock)
        {
            foreach ((int channel, byte value) in channels)
            {
                if (channel is < 1 or > ChannelCount)
                {
                    skipped++;
                    continue;
                }

                if (_flashing)
                {
                    _pendingDuringFlash[channel] = value;
                }
                else
                {
                    _frame[channel] = value;
                }
            }
        }

        return skipped;
    }

    /// <summary>Replaces the whole universe (index 0 = channel 1). Shorter input leaves the rest alone.</summary>
    public void SetUniverse(ReadOnlySpan<byte> values)
    {
        int count = Math.Min(values.Length, ChannelCount);

        lock (_frameLock)
        {
            if (_flashing)
            {
                for (int i = 0; i < count; i++)
                {
                    _pendingDuringFlash[i + 1] = values[i];
                }
            }
            else
            {
                values[..count].CopyTo(_frame.AsSpan(1));
            }
        }
    }

    public void ClearAll()
    {
        lock (_frameLock)
        {
            if (_flashing)
            {
                for (int channel = 1; channel <= ChannelCount; channel++)
                {
                    _pendingDuringFlash[channel] = 0;
                }
            }
            else
            {
                Array.Clear(_frame, 1, ChannelCount);
            }
        }
    }

    /// <summary>A copy of channel values 1-512 as they'll go out in the next frame.</summary>
    public byte[] Snapshot()
    {
        lock (_frameLock)
        {
            return _frame[1..];
        }
    }

    /// <summary>
    /// Turns every channel to full for <paramref name="duration"/>, then puts the frame back as it
    /// was. Values that arrive meanwhile are kept and win once the flash ends.
    /// </summary>
    public async Task FlashAsync(TimeSpan duration)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("The DMX device isn't open.");
        }

        byte[] before;

        lock (_frameLock)
        {
            before = (byte[])_frame.Clone();
            _flashing = true;
            Array.Fill<byte>(_frame, 255, 1, ChannelCount);
        }

        try
        {
            await Task.Delay(duration);
        }
        finally
        {
            lock (_frameLock)
            {
                _flashing = false;

                Array.Copy(before, _frame, _frame.Length);

                foreach ((int channel, byte value) in _pendingDuringFlash)
                {
                    _frame[channel] = value;
                }

                _pendingDuringFlash.Clear();
            }
        }
    }

    private void RunFrameLoop()
    {
        byte[] outgoing = new byte[ChannelCount + 1];
        long nextFrame = Stopwatch.GetTimestamp();

        while (!_stopRequested)
        {
            try
            {
                SendOneFrame(outgoing);
            }
            catch (Exception ex)
            {
                // Never let the loop die: dark lights are worse than a noisy log.
                _lastWriteError = "frame loop: " + ex.Message;
                _warn($"DMX frame loop error: {ex.Message}");
            }

            // Fixed-rate pacing that doesn't drift: aim at the next slot, and if a slow USB write
            // overran it, start again from now rather than firing a burst to catch up.
            nextFrame += (long)(FrameInterval.TotalSeconds * Stopwatch.Frequency);
            long now = Stopwatch.GetTimestamp();

            if (nextFrame < now)
            {
                nextFrame = now;
            }

            TimeSpan wait = Stopwatch.GetElapsedTime(now, nextFrame);

            if (wait > TimeSpan.Zero && !_stopRequested)
            {
                Thread.Sleep(wait);
            }
        }
    }

    /// <summary>One iteration of the frame loop, for tests built with runFrameThread: false.</summary>
    public void SendFrameNow() => SendOneFrame(new byte[ChannelCount + 1]);

    // One iteration of the frame thread: send the frame if the port is open, or try to get it back.
    private void SendOneFrame(byte[] outgoing)
    {
        IDmxPort? port;

        lock (_stateLock)
        {
            if (!_isOpen)
            {
                if (_wantedIndex is uint index && _time.GetUtcNow().UtcDateTime >= _nextReopenUtc)
                {
                    TryOpenPort(index);
                }

                if (!_isOpen)
                {
                    return;
                }
            }

            port = _port;
        }

        bool nonZero = false;

        lock (_frameLock)
        {
            Array.Copy(_frame, outgoing, outgoing.Length);
        }

        for (int i = 1; i < outgoing.Length && !nonZero; i++)
        {
            nonZero = outgoing[i] != 0;
        }

        string? error = port!.SendBreak() ?? port.Write(outgoing);

        if (error == null)
        {
            Interlocked.Increment(ref _framesWritten);
            _consecutiveFailures = 0;
            _lastWriteError = null;

            if (nonZero)
            {
                lock (_frameLock)
                {
                    _lastNonZeroFrameUtc = _time.GetUtcNow().UtcDateTime;
                }
            }

            return;
        }

        _lastWriteError = error;
        _consecutiveFailures++;

        if (_consecutiveFailures == 1)
        {
            _warn($"DMX frame failed: {error}");
        }

        if (_consecutiveFailures >= FailuresBeforeReopen)
        {
            lock (_stateLock)
            {
                _warn($"DMX frames failed {_consecutiveFailures} times in a row ({error}); closing the device and retrying every {ReopenInterval.TotalSeconds:0} s.");

                _port?.Close();
                _port = null;
                _isOpen = false;
                _consecutiveFailures = 0;
                _nextReopenUtc = _time.GetUtcNow().UtcDateTime + ReopenInterval;
            }
        }
    }

    /// <summary>Stops sending, blacks the rig out with one last all-zero frame, and releases the port.</summary>
    public void Close()
    {
        lock (_lifecycleLock)
        {
            StopThreadAndClosePort(blackout: true);
        }
    }

    // Caller holds _lifecycleLock (not _stateLock, which the frame thread needs to finish).
    private void StopThreadAndClosePort(bool blackout)
    {
        Thread? thread = _thread;
        _thread = null;
        _stopRequested = true;

        if (thread != null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromMilliseconds(500));
        }

        lock (_stateLock)
        {
            if (_port != null)
            {
                if (blackout)
                {
                    ClearAll();

                    byte[] dark = new byte[ChannelCount + 1];
                    _ = _port.SendBreak() ?? _port.Write(dark);
                }

                _port.Close();
                _port = null;
            }

            _isOpen = false;
            _wantedIndex = null;
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            StopThreadAndClosePort(blackout: true);
            _disposed = true;
        }
    }
}
