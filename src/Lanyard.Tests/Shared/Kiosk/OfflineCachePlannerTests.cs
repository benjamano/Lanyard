using Lanyard.Shared.DTO;
using Lanyard.Shared.Kiosk;
using Lanyard.Shared.Projection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Kiosk;

[TestClass]
public class OfflineCachePlannerTests
{
    private const long Mb = 1024 * 1024;

    private static readonly Guid VideoA = Guid.NewGuid();
    private static readonly Guid VideoB = Guid.NewGuid();
    private static readonly Guid VideoC = Guid.NewGuid();

    private static KioskOfflineStepDTO Video(Guid id, long mb) => new() { StepId = Guid.NewGuid(), TemplateName = ProjectionTemplateNames.PlayVideo, VideoFileId = id, VideoSizeBytes = mb * Mb };
    private static KioskOfflineStepDTO Text(string text) => new() { StepId = Guid.NewGuid(), TemplateName = ProjectionTemplateNames.ShowText, Text = text };

    private static KioskOfflineManifestDTO Manifest()
    {
        return new KioskOfflineManifestDTO
        {
            Displays =
            [
                // Listed out of order on purpose: screen 0's videos come first.
                new KioskOfflineDisplayDTO { DisplayIndex = 1, Steps = [Video(VideoC, 300)] },
                new KioskOfflineDisplayDTO { DisplayIndex = 0, Steps = [Text("Hi"), Video(VideoA, 100), Video(VideoB, 200), Video(VideoA, 100)] },
            ],
        };
    }

    [TestMethod]
    public void Plan_DownloadsEachMissingVideoOnce()
    {
        OfflineCachePlanner.CachePlan plan = OfflineCachePlanner.Plan(Manifest(), new Dictionary<Guid, long>(), 10_000 * Mb);

        CollectionAssert.AreEqual(new[] { VideoA, VideoB, VideoC }, plan.ToDownload.Select(x => x.VideoFileId!.Value).ToArray());
        Assert.AreEqual(600 * Mb, plan.KeptBytes);
        Assert.AreEqual(0, plan.DoesNotFit.Count);
    }

    [TestMethod]
    public void Plan_SkipsVideosAlreadyCachedAtTheRightSize_AndRefetchesBrokenOnes()
    {
        Dictionary<Guid, long> cached = new() { [VideoA] = 100 * Mb, [VideoB] = 12 };

        OfflineCachePlanner.CachePlan plan = OfflineCachePlanner.Plan(Manifest(), cached, 10_000 * Mb);

        CollectionAssert.AreEqual(new[] { VideoB, VideoC }, plan.ToDownload.Select(x => x.VideoFileId!.Value).ToArray());
        Assert.AreEqual(0, plan.ToDelete.Count);
    }

    [TestMethod]
    public void Plan_DeletesVideosTheManifestNoLongerUses()
    {
        Guid removed = Guid.NewGuid();
        Dictionary<Guid, long> cached = new() { [VideoA] = 100 * Mb, [removed] = 50 * Mb };

        OfflineCachePlanner.CachePlan plan = OfflineCachePlanner.Plan(Manifest(), cached, 10_000 * Mb);

        CollectionAssert.AreEqual(new[] { removed }, plan.ToDelete.ToArray());
    }

    [TestMethod]
    public void Plan_OverTheLimit_KeepsTheEarlierVideosAndDropsTheRest()
    {
        Dictionary<Guid, long> cached = new() { [VideoC] = 300 * Mb };

        OfflineCachePlanner.CachePlan plan = OfflineCachePlanner.Plan(Manifest(), cached, 250 * Mb);

        CollectionAssert.AreEqual(new[] { VideoA }, plan.Keep.ToArray());
        CollectionAssert.AreEquivalent(new[] { VideoB, VideoC }, plan.DoesNotFit.ToArray());
        CollectionAssert.AreEqual(new[] { VideoC }, plan.ToDelete.ToArray(), "A cached video that no longer fits is removed to free the space.");
    }

    [TestMethod]
    public void PlayableSteps_DropsVideosThatAreNotCached_AndKeepsTheRestInOrder()
    {
        KioskOfflineDisplayDTO display = Manifest().Displays.Single(x => x.DisplayIndex == 0);

        List<KioskOfflineStepDTO> playable = OfflineCachePlanner.PlayableSteps(display, new HashSet<Guid> { VideoA });

        CollectionAssert.AreEqual(
            new[] { ProjectionTemplateNames.ShowText, ProjectionTemplateNames.PlayVideo, ProjectionTemplateNames.PlayVideo },
            playable.Select(x => x.TemplateName).ToArray());
        Assert.IsTrue(playable.Where(x => x.VideoFileId != null).All(x => x.VideoFileId == VideoA));
    }
}
