namespace Lanyard.Application.Services.Clients;

/// <summary>
/// Thresholds for judging kiosk health from heartbeats, bound from "Kiosks:Health".
/// </summary>
public class KioskHealthOptions
{
    public const string SectionName = "Kiosks:Health";

    /// <summary>
    /// How often kiosks send a heartbeat. The client sends on a fixed 30 s timer
    /// (KioskHealthReporter.Interval); this must match it, or every kiosk looks unresponsive.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>Heartbeats missed in a row before a kiosk shows as unresponsive.</summary>
    public int MissedBeatsBeforeUnresponsive { get; set; } = 3;

    /// <summary>How much longer an unresponsive kiosk has to stay silent before it counts as offline.</summary>
    public int OfflineAfterMinutes { get; set; } = 5;

    public double MinFreeDiskGb { get; set; } = 5;
    public double MinFreeDiskPercent { get; set; } = 10;
    public double HighCpuPercent { get; set; } = 90;
    public double HighMemoryPercent { get; set; } = 90;

    public TimeSpan UnresponsiveAfter => TimeSpan.FromSeconds(HeartbeatIntervalSeconds * MissedBeatsBeforeUnresponsive);

    public TimeSpan OfflineAfter => UnresponsiveAfter + TimeSpan.FromMinutes(OfflineAfterMinutes);
}
