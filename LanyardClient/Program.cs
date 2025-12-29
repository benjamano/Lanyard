using Lanyard.Client.Controllers;
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
});

services.AddHttpClient();

services.AddSingleton<IMusicPlayer, MusicPlayer>();

services.AddSingleton<MusicControlHandler>();

ServiceProvider provider = services.BuildServiceProvider();

List<Action<HubConnection>> registrations =
[
    provider.GetRequiredService<MusicControlHandler>().Register
];

SignalRClient? signalRClient = new SignalRClient(serverUrl, registrations);

await signalRClient.StartAsync();

Console.WriteLine("Client running. Press Enter to exit.");
Console.ReadLine();