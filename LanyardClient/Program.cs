using Lanyard.Client.Controllers;
using Lanyard.Client.PacketSniffing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Console.WriteLine("▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄\r\n██ ████ ▄▄▀██ ▀██ ██ ███ █ ▄▄▀██ ▄▄▀██ ▄▄▀████ ▄▄▀██ ████▄ ▄██ ▄▄▄██ ▀██ █▄▄ ▄▄\r\n██ ████ ▀▀ ██ █ █ ██▄▀▀▀▄█ ▀▀ ██ ▀▀▄██ ██ ████ █████ █████ ███ ▄▄▄██ █ █ ███ ██\r\n██ ▀▀ █ ██ ██ ██▄ ████ ███ ██ ██ ██ ██ ▀▀ ████ ▀▀▄██ ▀▀ █▀ ▀██ ▀▀▀██ ██▄ ███ ██\r\n▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀");
Console.WriteLine("Starting...");

string? serverUrl = Environment.GetEnvironmentVariable("SIGNALR_SERVER_URL");
if (string.IsNullOrWhiteSpace(serverUrl))
{
    throw new Exception("SIGNALR_SERVER_URL is not set.");
}

ServiceCollection services = new ServiceCollection();

services.AddLogging(config =>
{
    config.ClearProviders();
    config.SetMinimumLevel(LogLevel.Information);
    config.AddConsole();
    config.AddDebug();
});

services.AddHttpClient();

services.AddSingleton<IPacketSniffer, PacketSniffer>();
services.AddSingleton<IActionFunctions, Actions>();
services.AddSingleton<IGameStateService, GameStateService>();

services.AddSingleton<IMusicPlayer, MusicPlayer>();
services.AddSingleton<MusicControlHandler>();

ServiceProvider provider = services.BuildServiceProvider();

List<Action<HubConnection>> registrations =
[
    provider.GetRequiredService<MusicControlHandler>().Register
];

SignalRClient? signalRClient = new(serverUrl, registrations);

await signalRClient.StartAsync();

IPacketSniffer sniffer = provider.GetRequiredService<IPacketSniffer>();

await sniffer.StartSniffingAsync();

Console.WriteLine("Client running. Press Enter to exit.");

bool stop = false;

while (stop == false)
{
    string? message = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(message) == false && int.TryParse(message, out int actionType))
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