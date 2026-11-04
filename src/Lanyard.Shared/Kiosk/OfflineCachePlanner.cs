using Lanyard.Shared.DTO;
using Lanyard.Shared.Projection;

namespace Lanyard.Shared.Kiosk;

/// <summary>
/// Works out which videos a kiosk should keep for its offline manifest, given what's already on
/// disk and its cache limit. Pure, so the WPF client's cache just carries out the plan.
/// </summary>
public static class OfflineCachePlanner
{
    public sealed record CachePlan(
        IReadOnlyList<KioskOfflineStepDTO> ToDownload,
        IReadOnlyList<Guid> Keep,
        IReadOnlyList<Guid> ToDelete,
        IReadOnlyList<Guid> DoesNotFit,
        long KeptBytes);

    /// <param name="cachedFiles">File id to size in bytes, for every video already on disk.</param>
    public static CachePlan Plan(KioskOfflineManifestDTO manifest, IReadOnlyDictionary<Guid, long> cachedFiles, long cacheLimitBytes)
    {
        List<KioskOfflineStepDTO> toDownload = [];
        List<Guid> keep = [];
        List<Guid> doesNotFit = [];
        long keptBytes = 0;

        // Screen order, then step order, so when space runs out it's the later videos that miss out.
        foreach (KioskOfflineStepDTO step in VideoSteps(manifest))
        {
            Guid fileId = step.VideoFileId!.Value;
            long size = step.VideoSizeBytes ?? 0;

            // A file on disk with the wrong size is a broken or half-written download: fetch it again.
            bool alreadyCached = cachedFiles.TryGetValue(fileId, out long cachedSize) && cachedSize == size;

            if (keptBytes + size > cacheLimitBytes)
            {
                doesNotFit.Add(fileId);
                continue;
            }

            keptBytes += size;
            keep.Add(fileId);

            if (!alreadyCached)
            {
                toDownload.Add(step);
            }
        }

        // Anything on disk the manifest no longer needs (an edited or deleted step), or that no
        // longer fits, goes.
        HashSet<Guid> keepSet = [.. keep];
        List<Guid> toDelete = [.. cachedFiles.Keys.Where(id => !keepSet.Contains(id))];

        return new CachePlan(toDownload, keep, toDelete, doesNotFit, keptBytes);
    }

    /// <summary>
    /// The steps a screen can actually play from this cache: video steps whose file isn't cached
    /// are dropped, everything else is kept in order.
    /// </summary>
    public static List<KioskOfflineStepDTO> PlayableSteps(KioskOfflineDisplayDTO display, IReadOnlySet<Guid> cachedFileIds)
    {
        return [.. display.Steps.Where(step =>
            step.TemplateName != ProjectionTemplateNames.PlayVideo
            || (step.VideoFileId is Guid id && cachedFileIds.Contains(id)))];
    }

    // One entry per distinct video, in the order it first appears.
    private static IEnumerable<KioskOfflineStepDTO> VideoSteps(KioskOfflineManifestDTO manifest)
    {
        HashSet<Guid> seen = [];

        foreach (KioskOfflineStepDTO step in manifest.Displays.OrderBy(x => x.DisplayIndex).SelectMany(x => x.Steps))
        {
            if (step.VideoFileId is Guid id && seen.Add(id))
            {
                yield return step;
            }
        }
    }
}
