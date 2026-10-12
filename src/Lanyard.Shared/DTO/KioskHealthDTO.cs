namespace Lanyard.Shared.DTO;

/// <summary>
/// Sent by the kiosk client every heartbeat (see KioskHealthReporter) through the hub method
/// "ReportKioskHealth". Only the latest report per kiosk is kept server-side. New fields must be
/// nullable or have a safe default, because older clients keep sending the old shape until they
/// restart and pick up an update.
/// </summary>
public class KioskHealthReportDTO
{
    public string? ClientVersion { get; set; }

    public DateTime ProcessStartedUtc { get; set; }
    public long MachineUptimeSeconds { get; set; }

    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }

    public long? SystemDriveFreeBytes { get; set; }
    public long? SystemDriveTotalBytes { get; set; }

    // "Configured" means the server asked the kiosk to use it, so configured-but-not-running
    // is the fault worth flagging. Kiosks that don't use DMX or capture report false for both.
    public bool DmxConfigured { get; set; }
    public bool DmxOpen { get; set; }

    /// <summary>Null when no preferred audio device is set for this kiosk.</summary>
    public bool? PreferredAudioDevicePresent { get; set; }

    public bool PacketCaptureConfigured { get; set; }
    public bool PacketCaptureRunning { get; set; }

    public int ProjectionRunningCount { get; set; }
}
