using Lanyard.Shared.Enum;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Lanyard.Client.RemoteRestart;

public class RemoteRestartService(ILogger<RemoteRestartService> logger) : IRemoteRestartService
{
    private const string WatchdogProcessName = "LanyardClient.Watchdog";

    // Must match RestartRequestedExitCode in LanyardClient.Watchdog/Program.cs: the Watchdog
    // relaunches on this code without counting it towards its crash-loop give-up.
    private const int RestartRequestedExitCode = 75;

    // Long enough for the log line to flush and for someone at the kiosk to see the
    // Windows "you're about to be signed out" notice, short enough to feel immediate.
    private const int ComputerRestartDelaySeconds = 10;

    // Gives the SignalR acknowledgement time to reach the server before the process exits.
    private static readonly TimeSpan AcknowledgementGrace = TimeSpan.FromSeconds(1);

    private readonly ILogger<RemoteRestartService> _logger = logger;

    public bool Restart(ClientRestartType restartType)
    {
        switch (restartType)
        {
            case ClientRestartType.Computer:
                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                {
                    _logger.LogWarning("Refusing remote PC restart; not running on Windows");
                    return false;
                }

                _ = RunAfterGraceAsync(RestartComputer);
                return true;

            case ClientRestartType.Application:
                _ = RunAfterGraceAsync(RestartApplication);
                return true;

            default:
                _logger.LogWarning("Refusing restart command with unknown restart type {RestartType}", restartType);
                return false;
        }
    }

    private static async Task RunAfterGraceAsync(Action action)
    {
        await Task.Delay(AcknowledgementGrace);
        action();
    }

    private void RestartComputer()
    {
        try
        {
            _logger.LogInformation("Restarting this PC in {Delay}s on request from the server", ComputerRestartDelaySeconds);

            // Standard users may restart their own machine, so no elevation is required
            // (same as the scheduled restart task in RestartSchedulerService).
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"/r /t {ComputerRestartDelaySeconds} /c \"Lanyard remote restart\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart this PC");
        }
    }

    private void RestartApplication()
    {
        try
        {
            // Under the Watchdog (every real kiosk install), exiting is enough: it relaunches
            // the client a few seconds later. Launching a second copy ourselves as well would
            // leave two clients fighting over the same client ID.
            bool watchdogRunning = Process.GetProcessesByName(WatchdogProcessName).Length > 0;

            if (watchdogRunning)
            {
                _logger.LogInformation("Restarting Lanyard Client on request from the server; the Watchdog will relaunch it");
            }
            else
            {
                string? exePath = Environment.ProcessPath;

                if (string.IsNullOrEmpty(exePath))
                {
                    _logger.LogWarning("Ignoring remote client restart; no Watchdog is running and the client's own path is unknown, so it could not come back up");
                    return;
                }

                _logger.LogInformation("Restarting Lanyard Client on request from the server; no Watchdog running, relaunching {ExePath} directly", exePath);

                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
            }

            Environment.Exit(RestartRequestedExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart Lanyard Client");
        }
    }
}
