using System.Diagnostics;
using FTD2XX_NET;
using Lanyard.Shared.Dmx;
using Microsoft.Extensions.Logging;

/// <summary>
/// Wraps the ENTTEC Open DMX USB device (FTDI-based). The frame buffer, the 40 Hz frame thread,
/// idempotent open and unplug recovery live in <see cref="DmxFrameOutput"/> (Lanyard.Shared, unit
/// tested); this class only adapts the FTDI driver to it.
/// </summary>
public class OpenDmxDevice : IDisposable
{
    // FTD2XX_NET's FTDI constructor can hang indefinitely on some machines/sessions (its
    // internal WinForms-based device-notification setup never completes) rather than fail
    // fast when no compatible device/driver context is available. Constructing it off-thread
    // with a timeout means a bad environment degrades DMX to "unavailable" instead of hanging
    // the whole client's startup/reconnect.
    private static readonly TimeSpan FtdiConstructTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<OpenDmxDevice> _logger;
    private readonly DmxFrameOutput _output;

    private readonly object _ftdiLock = new();
    private FTDI? _ftdi;

    // The one construction attempt. A timed-out attempt is kept and checked again later rather
    // than abandoned and replaced: each replacement used to leak another hung thread and cost
    // every caller (including the SignalR settings handler) another 5 s wait.
    private Task<FTDI>? _ftdiConstruction;
    private bool _ftdiFailed;

    private bool _disposed;

    public OpenDmxDevice(ILogger<OpenDmxDevice> logger)
    {
        _logger = logger;
        _output = new DmxFrameOutput(
            () => new FtdiDmxPort(GetFtdiOrThrow()),
            message => _logger.LogWarning("{DmxMessage}", message));
    }

    public long FramesWritten => _output.FramesWritten;

    /// <summary>The failure from the most recent frame, or null when it went out whole.</summary>
    public string? LastWriteError => _output.LastWriteError;

    /// <summary>When a frame with any channel above zero last went out; null if none has since the port opened.</summary>
    public DateTime? LastNonZeroFrameUtc => _output.LastNonZeroFrameUtc;

    public bool IsOpen => _output.IsOpen;

    private FTDI GetFtdiOrThrow() =>
        TryGetFtdi(out FTDI ftdi) ? ftdi : throw new InvalidOperationException("The FTDI driver interface isn't available.");

    private bool TryGetFtdi(out FTDI ftdi)
    {
        lock (_ftdiLock)
        {
            if (_ftdi != null)
            {
                ftdi = _ftdi;
                return true;
            }

            if (_ftdiFailed)
            {
                ftdi = null!;
                return false;
            }

            _ftdiConstruction ??= Task.Run(() => new FTDI());
        }

        try
        {
            if (!_ftdiConstruction.Wait(FtdiConstructTimeout))
            {
                _logger.LogWarning("Timed out constructing the FTDI driver interface after {Timeout}s. DMX is unavailable until it finishes.", FtdiConstructTimeout.TotalSeconds);
                ftdi = null!;
                return false;
            }

            lock (_ftdiLock)
            {
                _ftdi = _ftdiConstruction.Result;
                ftdi = _ftdi;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to construct the FTDI driver interface. DMX is unavailable for this session.");

            lock (_ftdiLock)
            {
                _ftdiFailed = true;
            }

            ftdi = null!;
            return false;
        }
    }

    /// <summary>
    /// Drives the device at the given FTDI index (default 0). Safe to call again with the same
    /// index - the server re-sends DMX settings on every reconnect - and it does nothing then.
    /// False when the device wouldn't open; it keeps retrying in the background.
    /// </summary>
    public bool Open(uint deviceIndex = 0) => _output.Open(deviceIndex);

    /// <summary>Sets a channel value (channels are 1-512; others are ignored).</summary>
    public void SetChannel(int channel, byte value) => _output.SetChannel(channel, value);

    /// <summary>Applies many channels at once. Returns how many were outside 1-512 and skipped.</summary>
    public int SetChannels(IEnumerable<(int Channel, byte Value)> channels) => _output.SetChannels(channels);

    /// <summary>Replaces all 512 channels (index 0 = channel 1).</summary>
    public void SetUniverse(byte[] values) => _output.SetUniverse(values);

    /// <summary>Clears all 512 channels to zero.</summary>
    public void ClearAll() => _output.ClearAll();

    /// <summary>
    /// Turns every channel to full for <paramref name="duration"/>, then puts the frame back as it
    /// was. Values the server sends meanwhile are kept and win once the flash ends.
    /// </summary>
    public Task FlashAsync(TimeSpan duration) => _output.FlashAsync(duration);

    /// <summary>
    /// The FTDI devices plugged in, without opening any, so it is safe while the port is in use.
    /// False when the driver itself can't be loaded.
    /// </summary>
    public bool TryListDevices(out List<(int Index, string? Description, string? SerialNumber)> devices)
    {
        devices = [];

        if (!TryGetDeviceNodes(out FTDI.FT_DEVICE_INFO_NODE[]? nodes))
        {
            return false;
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            // A device this process already has open reports blank strings; it is still there.
            devices.Add((i, nodes[i]?.Description, nodes[i]?.SerialNumber));
        }

        return true;
    }

    private bool TryGetDeviceNodes(out FTDI.FT_DEVICE_INFO_NODE[] nodes)
    {
        nodes = [];

        if (!TryGetFtdi(out FTDI ftdi))
        {
            return false;
        }

        lock (_ftdiLock)
        {
            uint count = 0;

            if (ftdi.GetNumberOfDevices(ref count) != FTDI.FT_STATUS.FT_OK || count == 0)
            {
                return true;
            }

            FTDI.FT_DEVICE_INFO_NODE[] found = new FTDI.FT_DEVICE_INFO_NODE[count];

            if (ftdi.GetDeviceList(found) == FTDI.FT_STATUS.FT_OK)
            {
                nodes = found;
            }

            return true;
        }
    }

    public void Close() => _output.Close();

    /// <summary>
    /// Device names for the server's device list. The server matches saved devices by name, so the
    /// format is unchanged ("FTDI Device {id}"), but the id now comes from each device's own entry
    /// in the device list. It used to be read with GetDeviceID on a handle that wasn't open, which
    /// reported the same (or a bogus) id for every device.
    /// </summary>
    public List<string> GetAvailableDevices()
    {
        List<string> devices = [];

        if (!TryGetDeviceNodes(out FTDI.FT_DEVICE_INFO_NODE[] nodes))
        {
            return devices;
        }

        foreach (FTDI.FT_DEVICE_INFO_NODE? node in nodes)
        {
            string name = $"FTDI Device {node?.ID ?? 0}";

            // Two identical interfaces share an id; keep their names apart.
            int sameName = devices.Count(d => d == name || d.StartsWith(name + " #", StringComparison.Ordinal));
            devices.Add(sameName == 0 ? name : $"{name} #{sameName + 1}");
        }

        return devices;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _output.Dispose();
            _disposed = true;
        }
    }

    /// <summary>The FTDI driver as a <see cref="IDmxPort"/>. Never throws; returns the failing status.</summary>
    private sealed class FtdiDmxPort(FTDI ftdi) : IDmxPort
    {
        // DMX512 wants a break of at least 92 us and a mark-after-break of at least 12 us. Each
        // SetBreak call is itself a USB round trip, so these spins only top up short ones. A spin,
        // not Thread.Sleep(1), which is 15.6 ms at Windows' default timer resolution.
        private static readonly TimeSpan BreakTime = TimeSpan.FromMicroseconds(150);
        private static readonly TimeSpan MarkAfterBreakTime = TimeSpan.FromMicroseconds(20);

        public string? Open(uint deviceIndex)
        {
            try
            {
                FTDI.FT_STATUS status = ftdi.OpenByIndex(deviceIndex);

                if (status != FTDI.FT_STATUS.FT_OK)
                {
                    return $"status {status} (is the Open DMX USB connected and its driver installed?)";
                }

                // DMX512 standard: 250 kbaud, 8 data bits, 2 stop bits, no parity, no flow control
                return Check("set baud rate", ftdi.SetBaudRate(250000))
                    ?? Check("set data characteristics", ftdi.SetDataCharacteristics(FTDI.FT_DATA_BITS.FT_BITS_8, FTDI.FT_STOP_BITS.FT_STOP_BITS_2, FTDI.FT_PARITY.FT_PARITY_NONE))
                    ?? Check("set flow control", ftdi.SetFlowControl(FTDI.FT_FLOW_CONTROL.FT_FLOW_NONE, 0, 0))
                    ?? Check("clear RTS", ftdi.SetRTS(false))
                    ?? Check("clear DTR", ftdi.SetDTR(false))
                    ?? Check("purge", ftdi.Purge((uint)(FTDI.FT_PURGE.FT_PURGE_RX | FTDI.FT_PURGE.FT_PURGE_TX)));
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public string? SendBreak()
        {
            try
            {
                string? error = Check("start break", ftdi.SetBreak(true));

                if (error != null)
                {
                    return error;
                }

                Spin(BreakTime);

                error = Check("end break", ftdi.SetBreak(false));
                Spin(MarkAfterBreakTime);

                return error;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public string? Write(byte[] frame)
        {
            try
            {
                uint written = 0;
                FTDI.FT_STATUS status = ftdi.Write(frame, frame.Length, ref written);

                return status != FTDI.FT_STATUS.FT_OK
                    ? $"status {status}"
                    : written != frame.Length ? $"only {written} of {frame.Length} bytes written" : null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public void Close()
        {
            try
            {
                if (ftdi.IsOpen)
                {
                    ftdi.Close();
                }
            }
            catch
            {
                // Unplugged mid-close; nothing left to release.
            }
        }

        private static string? Check(string what, FTDI.FT_STATUS status) =>
            status == FTDI.FT_STATUS.FT_OK ? null : $"{what}: status {status}";

        private static void Spin(TimeSpan duration)
        {
            long start = Stopwatch.GetTimestamp();

            while (Stopwatch.GetElapsedTime(start) < duration)
            {
                Thread.SpinWait(20);
            }
        }
    }
}
