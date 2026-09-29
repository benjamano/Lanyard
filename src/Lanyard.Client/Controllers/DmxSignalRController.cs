using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Lanyard.Infrastructure.DTO;
using NAudio.Wave;
using Lanyard.Shared.DTO;
using FTD2XX_NET;
using System.Text;
using Lanyard.Infrastructure.Models.Dmx;
using Lanyard.Infrastructure.DTO.Dmx;

namespace Lanyard.Client.Controllers;

public class DmxSignalRController(ILogger<DmxSignalRController> logger, DmxController dmxController)
{
    private readonly ILogger<DmxSignalRController> _logger = logger;
    private DmxController _dmxController = dmxController;
    private HubConnection? _connection;

    public void Register(HubConnection connection)
    {
        _connection = connection;

        connection.On<ClientDmxSettingsDTO>("ReceiveDmxSettings", settings =>
        {
            _logger.LogInformation("Received dmx settings: USB device index {DmxUsbDeviceIndex}, IsActive {IsActive}", settings.DmxUsbDeviceIndex, settings.IsActive);

            if (settings.IsActive && settings.DmxUsbDeviceIndex >= 0)
            {
                _dmxController.Open(settings.DmxUsbDeviceIndex);
            }
        });

        // Batched form: one message per scene step / desk push, applied to the frame in one go.
        connection.On<List<DmxChannel>>("ReceiveDmxChannelValues", channels =>
        {
            _logger.LogDebug("Received {Count} DMX channel values", channels.Count);

            _dmxController.SetChannels(channels);
        });

        // Single-channel form kept so a server that predates batching still drives the lights.
        connection.On<DmxChannel>("ReceiveDmxChannelValue", channel =>
        {
            _logger.LogDebug("Received DMX channel value: Channel {Channel}, Value {Value}", channel.Address, channel.Value);

            _dmxController.SetChannel(channel.Address, channel.Value);
        });
    }
}
