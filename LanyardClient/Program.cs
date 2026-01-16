using Lanyard.Client.Controllers;
using Lanyard.Client.PacketSniffing;
using Lanyard.Client.UI;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading;
using System.Windows;
using static System.Net.Mime.MediaTypeNames;
using Application = System.Windows.Application;

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

services.AddSingleton<WindowManager>();

ServiceProvider provider = services.BuildServiceProvider();

List<Action<HubConnection>> registrations =
[
    provider.GetRequiredService<MusicControlHandler>().Register
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

SignalRClient? signalRClient = new(serverUrl, clientId, registrations);

await signalRClient.StartAsync();

IPacketSniffer sniffer = provider.GetRequiredService<IPacketSniffer>();

await sniffer.StartSniffingAsync();

Console.WriteLine("Client running. Press Enter to exit or type 'show' to display window.");

bool stop = false;

while (stop == false)
{
    string? message = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(message) == false)
    {
        if (message.Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            Thread uiThread = new Thread(() =>
            {
                var window = new TestWindow();
                window.Show();
                System.Windows.Threading.Dispatcher.Run();
            });

            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
        }
        else if (int.TryParse(message, out int actionType))
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