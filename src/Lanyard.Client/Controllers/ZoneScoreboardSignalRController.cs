using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Lanyard.Infrastructure.DTO;
using NAudio.Wave;
using Lanyard.Shared.DTO;
using FTD2XX_NET;
using System.Text;
using Lanyard.Infrastructure.Models.Dmx;
using Lanyard.Infrastructure.DTO.ZoneScoreboard;
using Lanyard.Client.PacketSniffing;

namespace Lanyard.Client.Controllers;

public class ZoneScoreboardSignalRController(ILogger<ZoneScoreboardSignalRController> logger, IPacketSniffer packetSniffer)
{
    private readonly ILogger<ZoneScoreboardSignalRController> _logger = logger;
    private HubConnection? _connection;

    private readonly IPacketSniffer _packetSniffer = packetSniffer;

    public void Register(HubConnection connection)
    {
        _connection = connection;

        _connection.On<ZoneScoreboardSettingsDTO>("ReceiveZoneScoreboardSettings", (settings) =>
        {
            _logger.LogInformation("Received ZoneScoreboardSettingsUpdated event from server.");
            
            _packetSniffer.StartSniffingAsync(settings);
        });
    }
}
