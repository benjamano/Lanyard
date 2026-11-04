using System.Text;
using FTD2XX_NET;
using Microsoft.Extensions.Logging;
using Timer = System.Threading.Timer;

/// <summary>
/// Wraps the ENTTEC Open DMX USB device (FTDI-based) for sending DMX512 frames.
/// </summary>
public class OpenDmxDevice : IDisposable
{
    // FTD2XX_NET's FTDI constructor can hang indefinitely on some machines/sessions (its
    // internal WinForms-based device-notification setup never completes) rather than fail
    // fast when no compatible device/driver context is available. Constructing it off-thread
    // with a timeout means a bad environment degrades DMX to "unavailable" instead of hanging
    // the whole client's startup/reconnect.
    private static readonly TimeSpan FtdiConstructTimeout = TimeSpan.FromSeconds(5);

    private FTDI? _ftdi;
    private readonly byte[] _dmxData = new byte[513];
    private bool _isOpen;
    private bool _disposed;
    private readonly ILogger<OpenDmxDevice> _logger;

    private readonly object _ioLock = new();
    private Timer? _refreshTimer;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(25); // ~40Hz, matches DMX512's expected continuous refresh

    // Read by the self-test to prove frames are really going out, and to keep its flash from
    // cutting into a scene. Written under _ioLock.
    private long _framesWritten;
    private string? _lastWriteError;
    private bool _flashing;
    private readonly Dictionary<int, byte> _pendingDuringFlash = [];
    private DateTime? _lastNonZeroFrameUtc;

    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    /// <summary>The failure from the most recent frame, or null when it went out whole.</summary>
    public string? LastWriteError => _lastWriteError;

    /// <summary>When a frame with any channel above zero last went out; null if none has since the port opened.</summary>
    public DateTime? LastNonZeroFrameUtc
    {
        get
        {
            lock (_ioLock)
            {
                return _lastNonZeroFrameUtc;
            }
        }
    }

    public bool IsOpen => _isOpen;

    public OpenDmxDevice(ILogger<OpenDmxDevice> logger)
    {
        _logger = logger;
    }

    private bool TryGetFtdi(out FTDI ftdi)
    {
        if (_ftdi != null)
        {
            ftdi = _ftdi;
            return true;
        }

        try
        {
            Task<FTDI> constructTask = Task.Run(() => new FTDI());

            if (!constructTask.Wait(FtdiConstructTimeout))
            {
                _logger.LogWarning("Timed out constructing the FTDI driver interface after {Timeout}s. DMX is unavailable for this session.", FtdiConstructTimeout.TotalSeconds);
                ftdi = null!;
                return false;
            }

            _ftdi = constructTask.Result;
            ftdi = _ftdi;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to construct the FTDI driver interface. DMX is unavailable for this session.");
            ftdi = null!;
            return false;
        }
    }

    /// <summary>Opens the device at the given FTDI device index (default 0).</summary>
    public bool Open(uint deviceIndex = 0)
    {
        if (!TryGetFtdi(out FTDI ftdi))
        {
            return false;
        }

        var status = ftdi.OpenByIndex(deviceIndex);
        if (status != FTDI.FT_STATUS.FT_OK)
        {
            _logger.LogError("Failed to open FTDI device (status: {Status}). Check that the Open DMX USB is connected and drivers are installed.", status);
            return false;
        }

        // DMX512 standard: 250 kbaud, 8 data bits, 2 stop bits, no parity, no flow control
        ftdi.SetBaudRate(250000);
        ftdi.SetDataCharacteristics(
            FTDI.FT_DATA_BITS.FT_BITS_8,
            FTDI.FT_STOP_BITS.FT_STOP_BITS_2,
            FTDI.FT_PARITY.FT_PARITY_NONE);
        ftdi.SetFlowControl(FTDI.FT_FLOW_CONTROL.FT_FLOW_NONE, 0, 0);
        ftdi.SetRTS(false);
        ftdi.SetDTR(false);
        ftdi.Purge((uint)(FTDI.FT_PURGE.FT_PURGE_RX | FTDI.FT_PURGE.FT_PURGE_TX));

        _dmxData[0] = 0x00; // DMX null start code
        _lastWriteError = null;
        _isOpen = true;

        // DMX512 (E1.11) receivers time out (~1s) if the line goes quiet and fall back
        // to a manufacturer default (often full brightness). Resend the full frame
        // continuously so held/zero values are never silently dropped.
        _refreshTimer = new Timer(_ => SendFrame(), null, RefreshInterval, RefreshInterval);

        return true;
    }

    /// <summary>Sets a channel value (channels are 1–512).</summary>
    public void SetChannel(int channel, byte value)
    {
        if (channel < 1 || channel > 512)
            throw new ArgumentOutOfRangeException(nameof(channel), "Channel must be between 1 and 512.");

        lock (_ioLock)
        {
            if (_flashing)
            {
                _pendingDuringFlash[channel] = value;
                return;
            }

            _dmxData[channel] = value;
        }
    }

    /// <summary>Clears all 512 channels to zero.</summary>
    public void ClearAll()
    {
        lock (_ioLock)
        {
            if (_flashing)
            {
                for (int channel = 1; channel <= 512; channel++)
                {
                    _pendingDuringFlash[channel] = 0;
                }

                return;
            }

            Array.Clear(_dmxData, 1, 512);
        }
    }

    /// <summary>
    /// Sends one complete DMX512 frame:
    ///   Break (~1 ms) → Mark-After-Break (~1 ms) → start code + 512 bytes.
    /// </summary>
    public void SendFrame()
    {
        lock (_ioLock)
        {
            if (!_isOpen || _ftdi == null) return;

            _ftdi.SetBreak(true);
            Thread.Sleep(1);
            _ftdi.SetBreak(false);

            Thread.Sleep(1);

            uint written = 0;
            FTDI.FT_STATUS status = _ftdi.Write(_dmxData, _dmxData.Length, ref written);

            _lastWriteError = status != FTDI.FT_STATUS.FT_OK
                ? $"status {status}"
                : written != _dmxData.Length ? $"only {written} of {_dmxData.Length} bytes written" : null;

            if (_lastWriteError == null)
            {
                Interlocked.Increment(ref _framesWritten);
            }

            if (HasNonZeroChannel())
            {
                _lastNonZeroFrameUtc = DateTime.UtcNow;
            }
        }
    }

    private bool HasNonZeroChannel()
    {
        for (int i = 1; i < _dmxData.Length; i++)
        {
            if (_dmxData[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Turns every channel to full for <paramref name="duration"/>, then puts the frame back as it
    /// was. Values the server sends meanwhile are kept and win once the flash ends.
    /// </summary>
    public async Task FlashAsync(TimeSpan duration)
    {
        byte[] before;

        lock (_ioLock)
        {
            if (!_isOpen)
            {
                throw new InvalidOperationException("The DMX device isn't open.");
            }

            before = (byte[])_dmxData.Clone();
            _flashing = true;
            Array.Fill<byte>(_dmxData, 255, 1, 512);
        }

        try
        {
            await Task.Delay(duration);
        }
        finally
        {
            lock (_ioLock)
            {
                _flashing = false;

                // Channels the server changed during the flash were parked in _pendingDuringFlash.
                Array.Copy(before, _dmxData, _dmxData.Length);

                foreach ((int channel, byte value) in _pendingDuringFlash)
                {
                    _dmxData[channel] = value;
                }

                _pendingDuringFlash.Clear();
            }

            SendFrame();
        }
    }

    /// <summary>
    /// The FTDI devices plugged in, without opening any, so it is safe while the port is in use.
    /// False when the driver itself can't be loaded.
    /// </summary>
    public bool TryListDevices(out List<(int Index, string? Description, string? SerialNumber)> devices)
    {
        devices = [];

        if (!TryGetFtdi(out FTDI ftdi))
        {
            return false;
        }

        uint count = 0;

        if (ftdi.GetNumberOfDevices(ref count) != FTDI.FT_STATUS.FT_OK || count == 0)
        {
            return true;
        }

        FTDI.FT_DEVICE_INFO_NODE[] nodes = new FTDI.FT_DEVICE_INFO_NODE[count];

        if (ftdi.GetDeviceList(nodes) != FTDI.FT_STATUS.FT_OK)
        {
            return true;
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            // A device this process already has open reports blank strings; it is still there.
            devices.Add((i, nodes[i]?.Description, nodes[i]?.SerialNumber));
        }

        return true;
    }

    public void Close()
    {
        if (_isOpen)
        {
            using ManualResetEvent timerStopped = new(false);
            if (_refreshTimer != null && _refreshTimer.Dispose(timerStopped))
            {
                timerStopped.WaitOne(TimeSpan.FromMilliseconds(500));
            }
            _refreshTimer = null;

            ClearAll();
            SendFrame();
            _ftdi?.Close();
            _isOpen = false;
        }
    }

    public List<string> GetAvailableDevices()
    {
        List<string> devices = [];

        if (!TryGetFtdi(out FTDI ftdi))
        {
            return devices;
        }

        uint deviceCount = 0;

        FTDI.FT_STATUS status = ftdi.GetNumberOfDevices(ref deviceCount);

        if (status == FTDI.FT_STATUS.FT_OK && deviceCount > 0)
        {
            for (uint i = 0; i < deviceCount; i++)
            {
                uint deviceId = 0;

                ftdi.GetDeviceID(ref deviceId);

                string deviceName = $"FTDI Device {deviceId}";

                devices.Add(deviceName);
            }
        }

        return devices;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Close();
            _disposed = true;
        }
    }
}
