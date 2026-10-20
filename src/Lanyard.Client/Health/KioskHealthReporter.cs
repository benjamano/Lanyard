using Lanyard.Client.AudioDevices;
using Lanyard.Client.PacketSniffing;
using Lanyard.Client.ProjectionPrograms;
using Lanyard.Client.ProjectionPrograms.Offline;
using Lanyard.Client.SignalR;
using Lanyard.Shared.DTO;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using AutoUpdater = Lanyard.Client.AutoUpdate.AutoUpdate;

namespace Lanyard.Client.Health;

/// <summary>
/// Sends a heartbeat with this kiosk's health to the server every <see cref="Interval"/>, so the
/// server can tell a hung or crashed kiosk from a healthy one without waiting for SignalR to
/// notice the connection is gone.
/// </summary>
public class KioskHealthReporter(
    ILogger<KioskHealthReporter> logger,
    ISignalRClient signalRClient,
    DmxController dmxController,
    IPacketSniffer packetSniffer,
    IAudioDeviceService audioDeviceService,
    IProjectionProgramsService projectionProgramsService,
    IKioskOfflineCache offlineCache,
    KioskConnectivityMonitor connectivityMonitor)
{
    // The server's Kiosks:Health:HeartbeatIntervalSeconds must match this.
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ILogger<KioskHealthReporter> _logger = logger;
    private readonly ISignalRClient _signalRClient = signalRClient;
    private readonly DmxController _dmxController = dmxController;
    private readonly IPacketSniffer _packetSniffer = packetSniffer;
    private readonly IAudioDeviceService _audioDeviceService = audioDeviceService;
    private readonly IProjectionProgramsService _projectionProgramsService = projectionProgramsService;
    private readonly IKioskOfflineCache _offlineCache = offlineCache;
    private readonly KioskConnectivityMonitor _connectivityMonitor = connectivityMonitor;

    private readonly SystemMetrics _metrics = new();
    private readonly DateTime _processStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private readonly string? _version = ResolveVersion();

    private bool _warnedServerTooOld;
    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // Primes the CPU counter, so the first real report has a sample to compare against.
        _metrics.SampleCpuPercent();

        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        using PeriodicTimer timer = new(Interval);

        do
        {
            await SendOnceAsync();
        }
        while (await timer.WaitForNextTickAsync());
    }

    private async Task SendOnceAsync()
    {
        if (!_signalRClient.IsConnected)
        {
            return;
        }

        try
        {
            await _signalRClient.ReportKioskHealthAsync(BuildReport());
            _warnedServerTooOld = false;
        }
        catch (HubException ex)
        {
            // A server older than heartbeats has no ReportKioskHealth method. Say so once, not
            // every 30 seconds.
            if (!_warnedServerTooOld)
            {
                _logger.LogWarning("The server rejected the health heartbeat, it may be older than this client: {Message}", ex.Message);
                _warnedServerTooOld = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send the health heartbeat");
        }
    }

    private KioskHealthReportDTO BuildReport()
    {
        (long? freeBytes, long? totalBytes) = SystemMetrics.SystemDrive();
        KioskOfflineManifestDTO? manifest = _offlineCache.Manifest;

        return new KioskHealthReportDTO
        {
            ClientVersion = _version,
            ProcessStartedUtc = _processStartedUtc,
            MachineUptimeSeconds = Environment.TickCount64 / 1000,
            CpuPercent = _metrics.SampleCpuPercent(),
            MemoryPercent = SystemMetrics.MemoryPercent(),
            SystemDriveFreeBytes = freeBytes,
            SystemDriveTotalBytes = totalBytes,
            DmxConfigured = _dmxController.IsConfigured,
            DmxOpen = _dmxController.IsDeviceOpen,
            PreferredAudioDevicePresent = _audioDeviceService.PreferredDevicePresent,
            PacketCaptureConfigured = _packetSniffer.IsConfigured,
            PacketCaptureRunning = _packetSniffer.IsCapturing,
            ProjectionRunningCount = _projectionProgramsService.RunningCount,
            OfflineFallbackReady = OfflineFallbackReady(manifest),
            OfflineVideosNotCached = manifest is { OfflineFallbackEnabled: true } ? _offlineCache.VideosThatDoNotFit : null,
            OfflineCacheBytes = manifest is { OfflineFallbackEnabled: true } ? _offlineCache.CachedBytes : null,
            OfflineCacheSyncedUtc = manifest?.GeneratedUtc,
            LastOfflineStartedUtc = _connectivityMonitor.LastOfflineStartedUtc,
            LastOfflineEndedUtc = _connectivityMonitor.LastOfflineEndedUtc,
        };
    }

    // Null (rather than false) when there's nothing to be ready for: the fallback is off, no
    // programs are assigned, or the kiosk hasn't synced a copy yet.
    private bool? OfflineFallbackReady(KioskOfflineManifestDTO? manifest)
    {
        if (manifest is not { OfflineFallbackEnabled: true } || manifest.Displays.Count == 0)
        {
            return null;
        }

        return _offlineCache.VideosThatDoNotFit == 0;
    }

    // Velopack only knows the version when the app was installed through it; a dev build
    // falls back to the assembly version (the csproj <Version>).
    private static string? ResolveVersion()
    {
        try
        {
            string? installed = AutoUpdater.GetCurrentVersion();

            if (!string.IsNullOrWhiteSpace(installed))
            {
                return installed;
            }
        }
        catch (Exception)
        {
            // Not installed through Velopack.
        }

        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);
    }
}
