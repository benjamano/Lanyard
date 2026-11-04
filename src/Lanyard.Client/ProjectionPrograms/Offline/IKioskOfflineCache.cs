using Lanyard.Shared.DTO;

namespace Lanyard.Client.ProjectionPrograms.Offline;

/// <summary>
/// The kiosk's copy of its offline manifest and the videos in it, kept on disk so the screens can
/// play without the server, even straight after a reboot.
/// </summary>
public interface IKioskOfflineCache
{
    /// <summary>Folder holding the cached videos, mapped into the offline player.</summary>
    string MediaFolder { get; }

    /// <summary>The last manifest that was fully synced, or null when nothing has been cached yet.</summary>
    KioskOfflineManifestDTO? Manifest { get; }

    /// <summary>Videos on disk that the current manifest uses.</summary>
    IReadOnlySet<Guid> CachedVideoIds { get; }

    /// <summary>Videos the manifest uses that couldn't be cached because of the size limit.</summary>
    int VideosThatDoNotFit { get; }

    long CachedBytes { get; }

    /// <summary>Raised after a sync that changed the manifest (or the kiosk's offline settings).</summary>
    event Action<KioskOfflineManifestDTO>? ManifestChanged;

    /// <summary>
    /// Loads whatever was cached before the kiosk last stopped. Call once at startup, before the
    /// server is reachable.
    /// </summary>
    void LoadFromDisk();

    /// <summary>
    /// Fetches the manifest and any missing videos in the background. Calls made while a sync is
    /// already running are folded into one follow-up sync.
    /// </summary>
    void RequestSync();

    /// <summary>The file name a cached video is stored under in <see cref="MediaFolder"/>.</summary>
    static string VideoFileName(Guid fileId) => $"{fileId:N}.mp4";
}
