using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO.ZoneScoreboard;

public class ZoneScoreboardSettingsDTO
{
    public required string PreferredDeviceMacAddress { get; set; }

    public ZoneScoreboardVersion ZoneScoreboardVersion { get; set; }

    public string? DestinationIp { get; set; }
    public required string SourceIp { get; set; }

    // Defaults match how kiosks captured before these settings existed, so a server that doesn't
    // send them leaves a kiosk unchanged.
    public bool UseCaptureFilter { get; set; }
    public bool PromiscuousCapture { get; set; } = true;
}