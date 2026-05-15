using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Lanyard.Infrastructure.DTO;
using NAudio.Wave;
using Lanyard.Shared.DTO;
using FTD2XX_NET;
using System.Text;

namespace Lanyard.Client.Controllers;

public class DmxSignalRController(ILogger<DmxSignalRController> logger, DmxController dmxController)
{
    private readonly ILogger<DmxSignalRController> _logger = logger;
    private DmxController _dmxController = dmxController;
    private HubConnection? _connection;

    public void Register(HubConnection connection)
    {
        _connection = connection;

        connection.On<ClientDmxSettingsDTO>("RecieveDmxSettings", settings =>
        {
            _logger.LogInformation("Received dmx settings: USB device index {DmxUsbDeviceIndex}, IsActive {IsActive}", settings.DmxUsbDeviceIndex, settings.IsActive);

            if (settings.IsActive && settings.DmxUsbDeviceIndex > 0)
            {
                _dmxController.Open(settings.DmxUsbDeviceIndex);
            }
        });
    }
}
