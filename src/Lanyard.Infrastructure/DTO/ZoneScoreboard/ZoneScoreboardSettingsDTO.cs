using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO.ZoneScoreboard;

public class ZoneScoreboardSettingsDTO
{
    public required string PreferredDeviceMacAddress { get; set; }

    public ZoneScoreboardVersion ZoneScoreboardVersion { get; set; }

    public string? DestinationIp { get; set; }
    public required string SourceIp { get; set; }
}