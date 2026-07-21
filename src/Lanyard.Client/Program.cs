using Lanyard.Client.Controllers;
using Lanyard.Client.PacketSniffing;
using Lanyard.Client.ProjectionPrograms;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lanyard.Client.SignalR;
using Velopack;
using Lanyard.Client.AutoUpdate;

Console.WriteLine("▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄\r\n██ ████ ▄▄▀██ ▀██ ██ ███ █ ▄▄▀██ ▄▄▀██ ▄▄▀████ ▄▄▀██ ████▄ ▄██ ▄▄▄██ ▀██ █▄▄ ▄▄\r\n██ ████ ▀▀ ██ █ █ ██▄▀▀▀▄█ ▀▀ ██ ▀▀▄██ ██ ████ █████ █████ ███ ▄▄▄██ █ █ ███ ██\r\n██ ▀▀ █ ██ ██ ██▄ ████ ███ ██ ██ ██ ██ ▀▀ ████ ▀▀▄██ ▀▀ █▀ ▀██ ▀▀▀██ ██▄ ███ ██\r\n▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀");


if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development")
{
    VelopackApp.Build().Run();

    await AutoUpdate.CheckForUpdatesAsync();

    string version = AutoUpdate.GetCurrentVersion() ?? "Unknown Version";

    Console.WriteLine($"Lanyard Client {version}");
}

Console.WriteLine("Starting...");

VerifyEnvironmentVariables.Check();
StartupScheduler.EnsureStartupTaskExists();

ServiceProvider provider = ClientServiceBootstrapper.BuildServiceProvider();
List<Action<HubConnection>> registrations = ClientServiceBootstrapper.BuildHubRegistrations(provider);

Guid clientId = ClientIdentity.LoadOrCreateClientId();
ClientIdentity.ApplyToEnvironment(clientId);

await RunAsync(provider, registrations);

static async Task RunAsync(ServiceProvider provider, List<Action<HubConnection>> registrations)
{
    await StartupControls.ShowIfInterruptedAsync();

    ISignalRClient signalRClient = provider.GetRequiredService<ISignalRClient>();
    ILaserGameStatePublisher laserGameStatePublisher = provider.GetRequiredService<ILaserGameStatePublisher>();
    laserGameStatePublisher.Register();

    await signalRClient.Connect(registrations);
    await laserGameStatePublisher.PublishAsync();

    await Task.Delay(Timeout.Infinite);
}