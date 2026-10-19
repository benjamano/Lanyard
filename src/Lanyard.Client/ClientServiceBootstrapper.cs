using Lanyard.Client.AudioDevices;
using Lanyard.Client.Controllers;
using Lanyard.Client.Diagnostics;
using Lanyard.Client.Health;
using Lanyard.Client.PacketSniffing;
using Lanyard.Client.ProjectionPrograms;
using Lanyard.Client.ProjectionPrograms.Offline;
using Lanyard.Client.RemoteRestart;
using Lanyard.Client.RestartScheduler;
using Lanyard.Client.SelfTest;
using Lanyard.Client.SignalR;
using Lanyard.Client.VideoPublisher;
using Lanyard.Shared.Diagnostics;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

public static class ClientServiceBootstrapper
{
    public static ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new ServiceCollection();

        // Created up front because the logging provider below and the diagnostics controller
        // must share the same instance.
        LogLineBuffer logLineBuffer = new LogLineBuffer();
        string?[] knownSecrets = [Environment.GetEnvironmentVariable("LANYARD_CLIENT_SHARED_SECRET")];

        services.AddLogging(config =>
        {
            config.ClearProviders();
            config.SetMinimumLevel(LogLevel.Information);
            config.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
            config.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
                options.IncludeScopes = false;
                options.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Enabled;
            });

            config.AddOpenTelemetry(otel =>
            {
                otel.IncludeFormattedMessage = true;
                otel.IncludeScopes = true;
                otel.SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(serviceName: "Lanyard.Client", serviceInstanceId: Environment.MachineName));
                otel.AddOtlpExporter();
            });

            config.AddProvider(new RingBufferLoggerProvider(logLineBuffer, knownSecrets));
        });

        services.Configure<Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>(options =>
        {
            options.TimestampFormat = "HH:mm:ss ";
        });

        services.AddHttpClient();

        services.AddSingleton<IPacketSniffer, PacketSniffer>();
        services.AddSingleton<IActionFunctions, Actions>();
        services.AddSingleton<IGameStateService, GameStateService>();
        services.AddSingleton<ILaserGameStatePublisher, LaserGameStatePublisher>();
        services.AddSingleton<IProjectionProgramsService, ProjectionProgramsService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IKioskOfflineCache, KioskOfflineCache>();
        services.AddSingleton<IOfflineMusicService, OfflineMusicService>();
        services.AddSingleton<KioskConnectivityMonitor>();
        services.AddSingleton<DmxController, DmxController>();
        services.AddSingleton<OpenDmxDevice, OpenDmxDevice>();
        services.AddSingleton<ISignalRClient, SignalRClient>();
        services.AddSingleton<DmxSignalRController, DmxSignalRController>();
        services.AddSingleton<ISongCacheService, SongCacheService>();
        services.AddSingleton<MusicGain>();
        services.AddSingleton<IMusicDucker>(sp => sp.GetRequiredService<MusicGain>());
        services.AddSingleton<IMusicPlayer, MusicPlayer>();
        services.AddSingleton<IAudioClipCacheService, AudioClipCacheService>();
        services.AddSingleton<IAudioClipPlayer, AudioClipPlayer>();
        services.AddSingleton<AudioClipController>();
        services.AddSingleton<MusicControlHandler>();
        services.AddSingleton<ProjectionProgramController>();
        services.AddSingleton<ZoneScoreboardSignalRController>();
        services.AddSingleton<IVideoPublisherWindowService, VideoPublisherWindowService>();
        services.AddSingleton<VideoPublisherSignalRController>();
        services.AddSingleton<IRestartSchedulerService, RestartSchedulerService>();
        services.AddSingleton<RestartScheduleController>();
        services.AddSingleton<IRemoteRestartService, RemoteRestartService>();
        services.AddSingleton<RemoteRestartController>();
        services.AddSingleton<UpdateController>();
        services.AddSingleton<IAudioDeviceService, AudioDeviceService>();
        services.AddSingleton<AudioSettingsController>();
        services.AddSingleton(logLineBuffer);
        services.AddSingleton<ClientDiagnosticsController>();
        services.AddSingleton<KioskHealthReporter>();
        services.AddSingleton<IAudioEndpointProvider, AudioEndpointProvider>();
        services.AddSingleton<ITonePlayer, TonePlayer>();
        services.AddSingleton<ICaptureDeviceCatalog, CaptureDeviceCatalog>();
        services.AddSingleton<IWebView2RuntimeProbe, WebView2RuntimeProbe>();
        services.AddSingleton<KioskSelfTestRunner>();
        services.AddSingleton<KioskSelfTestController>();

        return services.BuildServiceProvider();
    }

    public static List<Action<HubConnection>> BuildHubRegistrations(ServiceProvider provider)
    {
        return
        [
            provider.GetRequiredService<MusicControlHandler>().Register,
            provider.GetRequiredService<AudioClipController>().Register,
            provider.GetRequiredService<ProjectionProgramController>().Register,
            provider.GetRequiredService<DmxSignalRController>().Register,
            provider.GetRequiredService<ZoneScoreboardSignalRController>().Register,
            provider.GetRequiredService<VideoPublisherSignalRController>().Register,
            provider.GetRequiredService<RestartScheduleController>().Register,
            provider.GetRequiredService<RemoteRestartController>().Register,
            provider.GetRequiredService<UpdateController>().Register,
            provider.GetRequiredService<AudioSettingsController>().Register,
            provider.GetRequiredService<ClientDiagnosticsController>().Register,
            provider.GetRequiredService<KioskSelfTestController>().Register
        ];
    }
}
