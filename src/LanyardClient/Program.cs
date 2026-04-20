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

string version = AutoUpdate.GetCurrentVersion() ?? "Unknown Version";

Console.WriteLine($"Lanyard Client V{version}");
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

// MAIN START HERE

async Task Main()
{
    if (await CountdownWithInterrupt())
    {
        ShowControls();
    }
    else
    {
        Console.WriteLine("No input detected. Continuing with normal startup...");
    }

    ISignalRClient signalRClient = provider.GetRequiredService<ISignalRClient>();
    ILaserGameStatePublisher laserGameStatePublisher = provider.GetRequiredService<ILaserGameStatePublisher>();
    laserGameStatePublisher.Register();

    await signalRClient.Connect(registrations);
    await laserGameStatePublisher.PublishAsync();

    IPacketSniffer sniffer = provider.GetRequiredService<IPacketSniffer>();

    await sniffer.StartSniffingAsync();

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
}

void ShowControls()
{
    int? option = null;

    while (option == null)
    {
        Console.WriteLine("Controls:");
        Console.WriteLine("1. Reset client ID (This will create a new client ID and disconnect from the server if already connected)");
        Console.WriteLine("2. Reset Config (This will clear all saved environment variables and ask for them again on next startup)");
        Console.WriteLine("3. Continue with normal startup");

        Console.WriteLine("Enter an option: ");

        string? input = Console.ReadLine();

        if (int.TryParse(input, out int selected))
        {
            option = selected;
            HandleControlOption(selected);
        }
        else
        {
            Console.WriteLine("Invalid input. Please enter a number corresponding to an option.");
        }
    }

    return;
}

void HandleControlOption(int option)
{
    switch (option)
    {
        case 1:
            VerifyEnvironmentVariables.ResetClientId();

            throw new Exception("Client ID reset. Please restart the application.");
        case 2:
            VerifyEnvironmentVariables.ResetConfig();

            throw new Exception("Config reset. Please restart the application.");
        case 3:
            Console.WriteLine("Continuing with normal startup...");
            break;
        default:
            Console.WriteLine("Unknown option selected.");
            break;
    }
}

async Task<bool> CountdownWithInterrupt()
{
    int countdown = 5;

    Console.WriteLine("Press any key to interrupt startup and access controls.");

    while (countdown > 0)
    {
        Console.WriteLine($"Starting in {countdown} seconds...");
        await Task.Delay(1000);
        countdown--;

        if (!Console.IsInputRedirected)
        {
            Console.WriteLine("Press any key to interrupt startup...");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (Console.KeyAvailable)
                {
                    Console.ReadKey(intercept: true);
                    Console.WriteLine("Startup interrupted. Accessing controls...");
                    return true;
                }
                await Task.Delay(50);
            }
        }
    }

    return false;
}

await Main();