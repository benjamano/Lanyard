using Microsoft.Build.Framework;
using Velopack;
using Velopack.Sources;

namespace Lanyard.Client.AutoUpdate;

internal class AutoUpdate
{
    internal static async Task CheckForUpdatesAsync()
    {
        UpdateManager mgr = new UpdateManager(new GithubSource("https://github.com/benjamano/Lanyard", null, false));

        if (mgr.IsInstalled == false)
        {
            return;
        }

        UpdateInfo? update = await mgr.CheckForUpdatesAsync();

        if (update == null) 
        {
            return; 
        }

        Console.WriteLine($"Update available: {update.TargetFullRelease.Version}. Downloading and applying update...");

        await mgr.DownloadUpdatesAsync(update);
        mgr.ApplyUpdatesAndRestart(update);
    }

    // Null when the client isn't a Velopack install (a dev build run from build\), so callers
    // can tell "unknown" apart from a real version instead of sending a placeholder string.
    internal static string? GetCurrentVersion()
    {
        try
        {
            UpdateManager mgr = new UpdateManager(new GithubSource("https://github.com/benjamano/Lanyard", null, false));
            return mgr.IsInstalled ? mgr.CurrentVersion?.ToString() : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not read the installed client version: {ex.Message}");
            return null;
        }
    }
}