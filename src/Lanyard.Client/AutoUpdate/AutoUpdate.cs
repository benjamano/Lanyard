using Velopack;
using Velopack.Locators;

namespace Lanyard.Client.AutoUpdate;

// Updates come from the Lanyard server, not straight from GitHub: the server decides which version
// each kiosk runs (stable, canary or a pinned version) and LanyardServerUpdateSource fetches that
// decision. Downgrades are allowed because the server's feed only ever holds the one version this
// kiosk should be on, so "older than current" can only mean an admin pinned it back or withdrew a
// bad release.
internal static class AutoUpdate
{
    private static readonly UpdateOptions Options = new() { AllowVersionDowngrade = true };

    // Set while an update is downloading, so a second "Update now" doesn't start another download.
    private static int _updateInProgress;

    // Startup path: the kiosk hasn't connected yet, so if there's an update it's applied here and
    // the kiosk restarts into it. Any failure (server down, bad feed) is logged and startup carries
    // on with the current version - an update problem must never stop a venue PC from starting.
    internal static async Task CheckAndApplyAtStartupAsync(Guid clientId)
    {
        try
        {
            UpdateManager? manager = CreateManager(clientId, out LanyardServerUpdateSource? source);

            if (manager is null || source is null)
            {
                return;
            }

            UpdateInfo? update = await manager.CheckForUpdatesAsync();

            if (update is null)
            {
                Console.WriteLine($"No update to apply (server target: {source.TargetVersion ?? "none"}).");
                return;
            }

            await DownloadAndApplyAsync(manager, update);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Update check failed, starting on the current version: {ex.Message}");
        }
    }

    // "Update now" from the Clients page. Answers as soon as it knows whether there's anything to
    // do, so the server isn't left waiting on a ~100 MB download; the download and restart then
    // carry on in the background.
    internal static async Task<bool> CheckOnRequestAsync(Guid clientId)
    {
        if (Interlocked.CompareExchange(ref _updateInProgress, 1, 0) != 0)
        {
            Console.WriteLine("Update requested, but one is already downloading.");
            return true;
        }

        bool started = false;

        try
        {
            UpdateManager? manager = CreateManager(clientId, out _);

            if (manager is null)
            {
                return false;
            }

            UpdateInfo? update = await manager.CheckForUpdatesAsync();

            if (update is null)
            {
                return false;
            }

            started = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    await DownloadAndApplyAsync(manager, update);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Requested update to {update.TargetFullRelease.Version} failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _updateInProgress, 0);
                }
            });

            return true;
        }
        finally
        {
            if (!started)
            {
                Interlocked.Exchange(ref _updateInProgress, 0);
            }
        }
    }

    // Null when the client isn't a Velopack install (a dev build run from build\), so callers
    // can tell "unknown" apart from a real version instead of sending a placeholder string.
    internal static string? GetCurrentVersion()
    {
        try
        {
            // Only set once VelopackApp.Build().Run() has run, which App skips in Development.
            return VelopackLocator.IsCurrentSet
                ? VelopackLocator.Current.CurrentlyInstalledVersion?.ToString()
                : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read the installed client version: {ex.Message}");
            return null;
        }
    }

    private static UpdateManager? CreateManager(Guid clientId, out LanyardServerUpdateSource? source)
    {
        source = null;

        if (!VelopackLocator.IsCurrentSet)
        {
            return null;
        }

        string? serverUrl = Environment.GetEnvironmentVariable("LANYARD_SERVER_URL");

        if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? serverBaseUrl))
        {
            Console.WriteLine("LANYARD_SERVER_URL isn't set to a valid URL; skipping the update check.");
            return null;
        }

        bool allowInsecureSsl = bool.TryParse(Environment.GetEnvironmentVariable("LANYARD_CLIENT_ALLOW_INSECURE_SSL"), out bool allow) && allow;

        source = new LanyardServerUpdateSource(serverBaseUrl, clientId, allowInsecureSsl);

        UpdateManager manager = new(source, Options);

        return manager.IsInstalled ? manager : null;
    }

    private static async Task DownloadAndApplyAsync(UpdateManager manager, UpdateInfo update)
    {
        string direction = update.IsDowngrade ? "Moving back" : "Updating";

        Console.WriteLine($"{direction} from {manager.CurrentVersion} to {update.TargetFullRelease.Version}. Downloading...");

        await manager.DownloadUpdatesAsync(update);

        Console.WriteLine($"Applying {update.TargetFullRelease.Version} and restarting.");

        manager.ApplyUpdatesAndRestart(update);
    }
}
