using Lanyard.Client.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public class DmxController
{
    private readonly OpenDmxDevice _device;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<DmxController> _logger;

    private bool _isDeviceOpen = false;

    public DmxController(IServiceScopeFactory serviceScopeFactory, ILogger<DmxController> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;

        _device = _serviceScopeFactory.CreateScope().ServiceProvider.GetRequiredService<OpenDmxDevice>();
    }

    public void Open(uint dmxUsbDeviceIndex)
    {
        if (!_device.Open(dmxUsbDeviceIndex))
        {
            throw new InvalidOperationException("Failed to open DMX device.");
        }

        _isDeviceOpen = true;

        _logger.LogInformation("DMX device opened successfully on USB index {DmxUsbDeviceIndex}", dmxUsbDeviceIndex);
    }

    public void SetChannel(int channel, byte value)
    {
        try
        {
            if (!_isDeviceOpen)
            {
                _logger.LogWarning("Attempted to set DMX channel value while device is not open. Channel: {Channel}, Value: {Value}", channel, value);
                return;
            }

            _device.SetChannel(channel, value);

            SendFrame();

            ISignalRClient _signalRClient = _serviceScopeFactory.CreateScope().ServiceProvider.GetRequiredService<ISignalRClient>();
            
            _signalRClient.SendDmxChannelValueAsync(channel, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting DMX channel value. Channel: {Channel}, Value: {Value}", channel, value);
        }
    }

    public void ClearAll()
    {
        try
        {
            if (!_isDeviceOpen)
            {
                _logger.LogWarning("Attempted to clear DMX channels while device is not open.");
                return;
            }

            _device.ClearAll();

            SendFrame();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing DMX channels.");
        }
    }

    public void SendFrame()
    {
        try
        {
            if (!_isDeviceOpen)
            {
                _logger.LogWarning("Attempted to send DMX frame while device is not open.");
                return;
            }

            _device.SendFrame();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending DMX frame.");
        }
    }

    public List<string> GetAvailableDevices()
    {
        return _device.GetAvailableDevices();
    }

    public void Close()
    {
        if (!_isDeviceOpen)
        {
            _logger.LogWarning("Attempted to close DMX device while it is not open.");
            return;
        }

        _device.Close();
        _isDeviceOpen = false;
    }
}