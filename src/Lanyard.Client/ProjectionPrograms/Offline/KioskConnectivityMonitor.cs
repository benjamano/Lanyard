using Lanyard.Client.SignalR;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Kiosk;
using Microsoft.Extensions.Logging;

namespace Lanyard.Client.ProjectionPrograms.Offline;

/// <summary>
/// Watches the hub connection and switches the screens (and music) to their cached copies when the
/// server has been unreachable for the kiosk's configured time, then hands back once the connection
/// has stayed up. The decisions are made by KioskConnectivityStateMachine; this class only feeds it
/// and carries them out. Also keeps the offline cache synced while connected.
/// </summary>
public class KioskConnectivityMonitor(
    ILogger<KioskConnectivityMonitor> logger,
    ISignalRClient signalRClient,
    IKioskOfflineCache offlineCache,
    IProjectionProgramsService projectionProgramsService,
    IOfflineMusicService offlineMusicService,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    // Catches edits the server doesn't announce (and anything missed while a sync was failing).
    private static readonly TimeSpan PeriodicSyncInterval = TimeSpan.FromMinutes(15);

    private readonly ILogger<KioskConnectivityMonitor> _logger = logger;
    private readonly ISignalRClient _signalRClient = signalRClient;
    private readonly IKioskOfflineCache _offlineCache = offlineCache;
    private readonly IProjectionProgramsService _projectionProgramsService = projectionProgramsService;
    private readonly IOfflineMusicService _offlineMusicService = offlineMusicService;
    private readonly TimeProvider _timeProvider = timeProvider;

    private readonly object _lock = new();
    private KioskConnectivityStateMachine? _machine;
    private DateTime _lastSyncUtc = DateTime.MinValue;
    private bool _started;

    public KioskConnectivityState State
    {
        get
        {
            lock (_lock)
            {
                return _machine?.State ?? KioskConnectivityState.Grace;
            }
        }
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        _offlineCache.LoadFromDisk();

        lock (_lock)
        {
            _machine = new KioskConnectivityStateMachine(now);
            ApplySettings(_offlineCache.Manifest, now);
        }

        _offlineCache.ManifestChanged += OnManifestChanged;
        _signalRClient.ConnectionStateChanged += OnConnectionStateChanged;

        _ = Task.Run(RunAsync);
    }

    private void OnConnectionStateChanged(bool connected)
    {
        lock (_lock)
        {
            _machine!.SetConnected(connected, _timeProvider.GetUtcNow().UtcDateTime);
        }

        if (connected)
        {
            _offlineCache.RequestSync();
            _lastSyncUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }
    }

    private void OnManifestChanged(KioskOfflineManifestDTO manifest)
    {
        KioskConnectivityTransition transition;

        lock (_lock)
        {
            transition = ApplySettings(manifest, _timeProvider.GetUtcNow().UtcDateTime);
        }

        // Only a "turned off while offline" can come out of a settings change.
        if (transition == KioskConnectivityTransition.ReturnedOnline)
        {
            _ = Task.Run(ReturnOnlineAsync);
        }
    }

    private KioskConnectivityTransition ApplySettings(KioskOfflineManifestDTO? manifest, DateTime nowUtc)
    {
        // Before the first sync there's nothing to play anyway; the defaults only decide timing.
        bool enabled = manifest?.OfflineFallbackEnabled ?? true;
        int seconds = manifest?.OfflineAfterSeconds ?? Lanyard.Shared.Contracts.KioskOfflineLimits.DefaultOfflineAfterSeconds;

        return _machine!.Configure(enabled, TimeSpan.FromSeconds(seconds), nowUtc);
    }

    private async Task RunAsync()
    {
        using PeriodicTimer timer = new(TickInterval, _timeProvider);

        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                await TickAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Offline: connectivity check failed");
            }
        }
    }

    private async Task TickAsync()
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        KioskConnectivityTransition transition;

        lock (_lock)
        {
            transition = _machine!.Tick(now, CanPlayOffline());
        }

        if (transition == KioskConnectivityTransition.EnteredOffline)
        {
            await GoOfflineAsync();
        }
        else if (transition == KioskConnectivityTransition.ReturnedOnline)
        {
            await ReturnOnlineAsync();
        }

        if (_signalRClient.IsConnected && now - _lastSyncUtc >= PeriodicSyncInterval)
        {
            _lastSyncUtc = now;
            _offlineCache.RequestSync();
        }
    }

    private bool CanPlayOffline()
    {
        KioskOfflineManifestDTO? manifest = _offlineCache.Manifest;

        return manifest is { OfflineFallbackEnabled: true } && manifest.Displays.Count > 0;
    }

    private async Task GoOfflineAsync()
    {
        KioskOfflineManifestDTO manifest = _offlineCache.Manifest!;

        _logger.LogWarning("Offline: no server for {Seconds} seconds, playing cached content (manifest {ManifestVersion})", manifest.OfflineAfterSeconds, manifest.ManifestVersion);

        await _projectionProgramsService.EnterOfflineModeAsync(manifest, _offlineCache.CachedVideoIds, _offlineCache.MediaFolder);
        await _offlineMusicService.EnterOfflineModeAsync();
    }

    private async Task ReturnOnlineAsync()
    {
        _offlineMusicService.ExitOfflineMode();
        await _projectionProgramsService.ExitOfflineModeAsync();
    }
}
