using Lanyard.Infrastructure.DTO.ZoneScoreboard;

namespace Lanyard.Client.PacketSniffing;

public interface IPacketSniffer
{
    Task StartSniffingAsync(ZoneScoreboardSettingsDTO settings);
    Task HandlePacketAsync(string[] decodedData);
}