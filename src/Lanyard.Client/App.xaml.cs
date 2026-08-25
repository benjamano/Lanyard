using Lanyard.Client.PacketSniffing;
using Lanyard.Client.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using Velopack;
using Application = System.Windows.Application;
using AutoUpdater = Lanyard.Client.AutoUpdate.AutoUpdate;

namespace Lanyard.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Projection windows come and go independently of each other; the app must keep
        // running (SignalR/DMX/music/packet-sniffing) even when zero windows are open.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Runs on a background thread rather than the WPF dispatcher thread: startup does
        // blocking, non-UI work (Task Scheduler registration, DI graph construction, the
        // initial SignalR connect) that has no business holding up the dispatcher. Window
        // creation later stays on the dispatcher thread regardless, via explicit
        // Dispatcher.InvokeAsync calls in ProjectionProgramsService.
        _ = Task.Run(RunStartupAsync);
    }

    private static async Task RunStartupAsync()
    {
        try
        {
            Console.WriteLine("▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄\r\n██ ████ ▄▄▀██ ▀██ ██ ███ █ ▄▄▀██ ▄▄▀██ ▄▄▀████ ▄▄▀██ ████▄ ▄██ ▄▄▄██ ▀██ █▄▄ ▄▄\r\n██ ████ ▀▀ ██ █ █ ██▄▀▀▀▄█ ▀▀ ██ ▀▀▄██ ██ ████ █████ █████ ███ ▄▄▄██ █ █ ███ ██\r\n██ ▀▀ █ ██ ██ ██▄ ████ ███ ██ ██ ██ ██ ▀▀ ████ ▀▀▄██ ▀▀ █▀ ▀██ ▀▀▀██ ██▄ ███ ██\r\n▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀");

            if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development")
            {
                VelopackApp.Build().Run();

                await AutoUpdater.CheckForUpdatesAsync();

                string version = AutoUpdater.GetCurrentVersion() ?? "Unknown Version";

                Console.WriteLine($"Lanyard Client {version}");
            }

            Console.WriteLine("Starting...");

            VerifyEnvironmentVariables.Check();
            StartupScheduler.EnsureStartupTaskExists();

            ServiceProvider provider = ClientServiceBootstrapper.BuildServiceProvider();
            List<Action<HubConnection>> registrations = ClientServiceBootstrapper.BuildHubRegistrations(provider);

            Guid clientId = ClientIdentity.LoadOrCreateClientId();
            ClientIdentity.ApplyToEnvironment(clientId);

            await StartupControls.ShowIfInterruptedAsync();

            ISignalRClient signalRClient = provider.GetRequiredService<ISignalRClient>();
            ILaserGameStatePublisher laserGameStatePublisher = provider.GetRequiredService<ILaserGameStatePublisher>();
            laserGameStatePublisher.Register();

            await signalRClient.Connect(registrations);
            await laserGameStatePublisher.PublishAsync();
        }
        catch (Exception ex)
        {
            // Startup failures must terminate the process (not just fault a background task) so
            // the Watchdog's crash-loop guard sees a real exit, matching the original console app.
            Console.WriteLine($"Fatal startup error: {ex}");
            Environment.Exit(1);
        }
    }
}
