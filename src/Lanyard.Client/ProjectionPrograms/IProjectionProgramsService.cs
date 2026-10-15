using Lanyard.Shared.DTO;
using System;
using System.Collections.Generic;
using System.Text;

namespace Lanyard.Client.ProjectionPrograms;

public interface IProjectionProgramsService
{
    Task StartProjectingAsync(IEnumerable<ClientProjectionSettingsDTO> projectionPrograms);
    Task TriggerTemporaryProjectionProgramAsync(Guid projectionProgramId, int? displayIndex, Func<Task> onCompleted);
    Task CloseWindowForDisplayAsync(int displayIndex);

    /// <summary>How many projection windows are open right now.</summary>
    int RunningCount { get; }

    /// <summary>True while the screens are playing their cached programs instead of the server's pages.</summary>
    bool IsOffline { get; }

    /// <summary>
    /// Replaces every projection window with the offline player, one per screen in the manifest.
    /// Server pushes received while offline are remembered and used when handing back.
    /// </summary>
    Task EnterOfflineModeAsync(KioskOfflineManifestDTO manifest, IReadOnlySet<Guid> cachedVideoIds, string mediaFolder);

    /// <summary>Stops offline playback and reopens the server's pages from the last programs it sent.</summary>
    Task ExitOfflineModeAsync();
}
