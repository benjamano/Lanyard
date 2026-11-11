using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.Extensions.Logging;

public class DmxController(OpenDmxDevice device, ILogger<DmxController> logger)
{
    private readonly OpenDmxDevice _device = device;
    private readonly ILogger<DmxController> _logger = logger;

    private volatile bool _isConfigured = false;
    private int _warnedNotOpen;

    /// <summary>
    /// True while frames are going out. Can turn true on its own: an interface that wouldn't open,
    /// or was unplugged, is retried in the background.
    /// </summary>
    public bool IsDeviceOpen => _device.IsOpen;

    /// <summary>True once the server has told this kiosk to drive a DMX device, even if opening it failed.</summary>
    public bool IsConfigured => _isConfigured;

    /// <summary>The USB index the server last asked for, or null before any DMX settings arrive.</summary>
    public uint? ConfiguredIndex { get; private set; }

    public long FramesWritten => _device.FramesWritten;

    public string? LastWriteError => _device.LastWriteError;

    public DateTime? LastNonZeroFrameUtc => _device.LastNonZeroFrameUtc;

    /// <summary>
    /// Starts driving the interface at the given index. Called on every SignalR (re)connect with
    /// the same settings, which is now a no-op once it's open. Never throws: it used to throw
    /// inside the SignalR handler, where nothing caught it usefully.
    /// </summary>
    public bool Open(uint dmxUsbDeviceIndex)
    {
        _isConfigured = true;
        ConfiguredIndex = dmxUsbDeviceIndex;

        try
        {
            bool opened = _device.Open(dmxUsbDeviceIndex);

            if (opened)
            {
                Interlocked.Exchange(ref _warnedNotOpen, 0);
                _logger.LogInformation("DMX device on USB index {DmxUsbDeviceIndex} is open", dmxUsbDeviceIndex);
            }
            else
            {
                _logger.LogWarning("DMX device on USB index {DmxUsbDeviceIndex} didn't open ({Error}); retrying in the background", dmxUsbDeviceIndex, _device.LastWriteError);
            }

            return opened;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening DMX device on USB index {DmxUsbDeviceIndex}", dmxUsbDeviceIndex);
            return false;
        }
    }

    // Values only ever arrive here from the server (there is no local fader input on the
    // kiosk), so nothing is echoed back. They always go into the frame, open or not, so a device
    // that opens late (or comes back after an unplug) starts with the right levels.
    public void SetChannel(int channel, byte value)
    {
        WarnIfNotOpen();
        _device.SetChannel(channel, value);
    }

    /// <summary>Applies a whole batch to the frame; the frame thread sends it within ~25 ms.</summary>
    public void SetChannels(IReadOnlyList<DmxChannel> channels)
    {
        WarnIfNotOpen();

        int skipped = _device.SetChannels(channels.Select(c => (c.Address, c.Value)));

        if (skipped > 0)
        {
            _logger.LogWarning("Ignored {Skipped} of {Count} DMX values outside channels 1-512", skipped, channels.Count);
        }
    }

    /// <summary>Replaces all 512 channels, for the server's full-universe resync.</summary>
    public void SetUniverse(byte[] values)
    {
        WarnIfNotOpen();

        if (values.Length != OpenDmxDeviceUniverse.Size)
        {
            _logger.LogWarning("DMX universe resync had {Length} channels, expected {Expected}; applying what fits", values.Length, OpenDmxDeviceUniverse.Size);
        }

        _device.SetUniverse(values);
    }

    public void ClearAll()
    {
        _device.ClearAll();
    }

    // Once per closed spell, not once per message: a scene would otherwise log 40 times a second.
    private void WarnIfNotOpen()
    {
        if (!_device.IsOpen && Interlocked.Exchange(ref _warnedNotOpen, 1) == 0)
        {
            _logger.LogWarning("DMX values arrived while the device isn't open; keeping them for when it is");
        }
        else if (_device.IsOpen)
        {
            Interlocked.Exchange(ref _warnedNotOpen, 0);
        }
    }

    public void MarkNotConfigured()
    {
        _isConfigured = false;
    }

    /// <summary>For the self-test: lights every channel briefly, then restores the frame.</summary>
    public Task FlashAsync(TimeSpan duration)
    {
        return _device.FlashAsync(duration);
    }

    public bool TryListDevices(out List<(int Index, string? Description, string? SerialNumber)> devices)
    {
        return _device.TryListDevices(out devices);
    }

    public List<string> GetAvailableDevices()
    {
        return _device.GetAvailableDevices();
    }

    public void Close()
    {
        _device.Close();
    }
}

internal static class OpenDmxDeviceUniverse
{
    public const int Size = 512;
}
