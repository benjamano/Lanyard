using Lanyard.Infrastructure.DTO.ZoneScoreboard;

namespace Lanyard.Client.PacketSniffing;

public interface IPacketSniffer
{
    Task StartSniffingAsync(ZoneScoreboardSettingsDTO settings);

    /// <summary>True once the server has sent zone scoreboard settings, even if capture failed to start.</summary>
    bool IsConfigured { get; }
    bool IsCapturing { get; }

    /// <summary>The MAC address the server chose, as sent; null before any settings arrive.</summary>
    string? PreferredMacAddress { get; }

    /// <summary>The Npcap name of the card being read, which may be a fallback when the chosen one is missing.</summary>
    string? ActiveDeviceName { get; }

    DateTime? CaptureStartedUtc { get; }

    /// <summary>
    /// The BPF filter in use, or null when every packet is captured. With a filter, only game
    /// packets reach the kiosk, so <see cref="LastPacketUtc"/> only moves when game packets arrive.
    /// </summary>
    string? CaptureFilter { get; }

    /// <summary>Any packet at all, which shows the card is plugged into something.</summary>
    DateTime? LastPacketUtc { get; }

    /// <summary>A packet from the laser tag system, which shows the card is on the right network.</summary>
    DateTime? LastGamePacketUtc { get; }

    /// <summary>Handles one decoded game packet. Called on the capture thread, so packets stay in order.</summary>
    void HandlePacket(string[] decodedData);
}