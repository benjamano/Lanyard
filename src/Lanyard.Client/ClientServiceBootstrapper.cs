using Lanyard.Client.Controllers;
using Lanyard.Client.PacketSniffing;
using Lanyard.Client.ProjectionPrograms;
using Lanyard.Client.SignalR;
using Lanyard.Client.VideoPublisher;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class ClientServiceBootstrapper
{
    public static ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new ServiceCollection();

        services.AddLogging(config =>
        {
            config.ClearProviders();
            config.SetMinimumLevel(LogLevel.Information);
            config.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
                options.IncludeScopes = false;
                options.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Enabled;
            });
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
        services.AddSingleton<DmxController, DmxController>();
        services.AddSingleton<OpenDmxDevice, OpenDmxDevice>();
        services.AddSingleton<ISignalRClient, SignalRClient>();
        services.AddSingleton<DmxSignalRController, DmxSignalRController>();
        services.AddSingleton<ISongCacheService, SongCacheService>();
        services.AddSingleton<IMusicPlayer, MusicPlayer>();
        services.AddSingleton<MusicControlHandler>();
        services.AddSingleton<ProjectionProgramController>();
        services.AddSingleton<ZoneScoreboardSignalRController>();
        services.AddSingleton<IVideoPublisherWindowService, VideoPublisherWindowService>();
        services.AddSingleton<VideoPublisherSignalRController>();

        return services.BuildServiceProvider();
    }

    public static List<Action<HubConnection>> BuildHubRegistrations(ServiceProvider provider)
    {
        return
        [
            provider.GetRequiredService<MusicControlHandler>().Register,
            provider.GetRequiredService<ProjectionProgramController>().Register,
            provider.GetRequiredService<DmxSignalRController>().Register,
            provider.GetRequiredService<ZoneScoreboardSignalRController>().Register,
            provider.GetRequiredService<VideoPublisherSignalRController>().Register
        ];
    }
}