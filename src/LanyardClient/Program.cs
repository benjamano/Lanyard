using Lanyard.Client.Controllers;
using Lanyard.Client.PacketSniffing;
using Lanyard.Client.ProjectionPrograms;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lanyard.Client.SignalR;
using Velopack;
using Lanyard.Client.AutoUpdate;
using System.Text.Json;

VelopackApp.Build().Run();

if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development")
{
    await AutoUpdate.CheckForUpdatesAsync();
}

Console.WriteLine("▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄\r\n██ ████ ▄▄▀██ ▀██ ██ ███ █ ▄▄▀██ ▄▄▀██ ▄▄▀████ ▄▄▀██ ████▄ ▄██ ▄▄▄██ ▀██ █▄▄ ▄▄\r\n██ ████ ▀▀ ██ █ █ ██▄▀▀▀▄█ ▀▀ ██ ▀▀▄██ ██ ████ █████ █████ ███ ▄▄▄██ █ █ ███ ██\r\n██ ▀▀ █ ██ ██ ██▄ ████ ███ ██ ██ ██ ██ ▀▀ ████ ▀▀▄██ ▀▀ █▀ ▀██ ▀▀▀██ ██▄ ███ ██\r\n▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀");
Console.WriteLine("Starting...");

VerifyEnvironmentVariables.Check();

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
        options.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Disabled;
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

services.AddSingleton<ISignalRClient, SignalRClient>();

services.AddSingleton<ISongCacheService, SongCacheService>();
services.AddSingleton<IMusicPlayer, MusicPlayer>();
services.AddSingleton<MusicControlHandler>();
services.AddSingleton<ProjectionProgramController>();

ServiceProvider provider = services.BuildServiceProvider();

List<Action<HubConnection>> registrations =
[
    provider.GetRequiredService<MusicControlHandler>().Register,
    provider.GetRequiredService<ProjectionProgramController>().Register
];

string? baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "lanyardClient");

Directory.CreateDirectory(baseDir);

string path = Path.Combine(baseDir, "client-id.txt");

Guid clientId;

if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path), out var saved))
{
    clientId = saved;
}
else
{
    clientId = Guid.NewGuid();
    File.WriteAllText(path, clientId.ToString());
}

Environment.SetEnvironmentVariable("LANYARD_CLIENT_ID", clientId.ToString());

ISignalRClient signalRClient = provider.GetRequiredService<ISignalRClient>();
ILaserGameStatePublisher laserGameStatePublisher = provider.GetRequiredService<ILaserGameStatePublisher>();
laserGameStatePublisher.Register();

await signalRClient.Connect(Environment.GetEnvironmentVariable("SIGNALR_SERVER_URL")!, clientId, registrations);
await laserGameStatePublisher.PublishAsync();

IPacketSniffer sniffer = provider.GetRequiredService<IPacketSniffer>();

await sniffer.StartSniffingAsync();

Console.WriteLine("Client running.");

bool stop = false;

while (stop == false)
{
    string? message = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(message) == false)
    {
        if (int.TryParse(message, out int actionType))
        {
            switch (actionType)
            {
                case 1:
                    // SEND A TEST PACKET WITH 10 MINUTES REMAINING

                    Random random = new();

                    sniffer.HandlePacketAsync(["4", "@015", "0"]);

                    sniffer.HandlePacketAsync(["1", "0", "0", "600"]);

                    sniffer.HandlePacketAsync(["3", "1", "0", random.Next(1, 201).ToString(), "0", "0", "0", random.Next(1, 101).ToString()]);

                    sniffer.HandlePacketAsync(["2", "0", random.Next(1, 101).ToString()]);

                    sniffer.HandlePacketAsync(["2", "2", random.Next(1, 101).ToString()]);

                    sniffer.HandlePacketAsync(["3", "3", "0", random.Next(1, 201).ToString(), "0", "0", "0", random.Next(1, 101).ToString()]);

                    sniffer.HandlePacketAsync(["3", "7", "0", random.Next(1, 201).ToString(), "0", "0", "0", random.Next(1, 101).ToString()]);

                    sniffer.HandlePacketAsync(["4", "@014", "0"]);

                    break;
                default:
                    Console.WriteLine("Unknown action.");
                    break;
            }
        }
        else
        {
            stop = true;
        }
    }
    else
    {
        stop = true;
    }
}
