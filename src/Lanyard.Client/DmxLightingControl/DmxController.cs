using Microsoft.Extensions.DependencyInjection;

public class DmxController
{
    private readonly OpenDmxDevice _device;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public DmxController(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
        
        _device = _serviceScopeFactory.CreateScope().ServiceProvider.GetRequiredService<OpenDmxDevice>();
    }

    public void Open(int dmxUsbDeviceIndex)
    {
        if (!_device.Open((uint)dmxUsbDeviceIndex))
        {
            throw new InvalidOperationException("Failed to open DMX device.");
        }
    }

    public void SetChannel(int channel, byte value) => _device.SetChannel(channel, value);

    public void ClearAll() => _device.ClearAll();

    public void SendFrame() => _device.SendFrame();

    public List<string> GetAvailableDevices()
    {
        return _device.GetAvailableDevices();
    }

    public void Close() => _device.Close();
}