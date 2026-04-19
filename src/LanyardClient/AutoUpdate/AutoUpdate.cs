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

        await mgr.DownloadUpdatesAsync(update);
        mgr.ApplyUpdatesAndRestart(update);
    }
}