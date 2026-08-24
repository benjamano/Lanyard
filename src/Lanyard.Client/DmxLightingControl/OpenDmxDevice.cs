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
            _dmxData[channel] = value;
        }
    }

    /// <summary>Clears all 512 channels to zero.</summary>
    public void ClearAll()
    {
        lock (_ioLock)
        {
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
            _ftdi.Write(_dmxData, _dmxData.Length, ref written);
        }
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
