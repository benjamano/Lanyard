using Lanyard.Infrastructure.DTO.ZoneScoreboard;

namespace Lanyard.Client.PacketSniffing;

public interface IPacketSniffer
{
    Task StartSniffingAsync(ZoneScoreboardSettingsDTO settings);

    /// <summary>True once the server has sent zone scoreboard settings, even if capture failed to start.</summary>
    bool IsConfigured { get; }
    bool IsCapturing { get; }
    Task HandlePacketAsync(string[] decodedData);
}