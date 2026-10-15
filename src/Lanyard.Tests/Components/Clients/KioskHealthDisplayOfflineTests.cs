using Lanyard.App.Components.Clients;
using Lanyard.Application.Services.Clients;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Components.Clients;

[TestClass]
public class KioskHealthDisplayOfflineTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static ClientHealthSnapshot Snapshot(Action<ClientHealthSnapshot> configure)
    {
        ClientHealthSnapshot snapshot = new() { LastHeartbeatUtc = Now };
        configure(snapshot);
        return snapshot;
    }

    [TestMethod]
    public void OfflineFallback_DescribesEachState()
    {
        Assert.AreEqual("Ready (180 MB of video cached)", KioskHealthDisplay.OfflineFallback(Snapshot(s => { s.OfflineFallbackReady = true; s.OfflineCacheBytes = 180L * 1024 * 1024; })));
        Assert.AreEqual("Ready (2.5 GB of video cached)", KioskHealthDisplay.OfflineFallback(Snapshot(s => { s.OfflineFallbackReady = true; s.OfflineCacheBytes = 2560L * 1024 * 1024; })));
        Assert.AreEqual("Not ready: 1 video doesn't fit the cache", KioskHealthDisplay.OfflineFallback(Snapshot(s => { s.OfflineFallbackReady = false; s.OfflineVideosNotCached = 1; })));
        Assert.AreEqual("Not ready", KioskHealthDisplay.OfflineFallback(Snapshot(s => s.OfflineFallbackReady = false)));
        Assert.AreEqual("Off, or nothing to play offline", KioskHealthDisplay.OfflineFallback(Snapshot(_ => { })));
    }

    [TestMethod]
    public void LastOutage_ShowsWhenAndHowLong()
    {
        ClientHealthSnapshot snapshot = Snapshot(s =>
        {
            s.LastOfflineStartedUtc = Now.AddHours(-3);
            s.LastOfflineEndedUtc = Now.AddHours(-3).AddMinutes(12);
        });

        Assert.AreEqual("3 hours ago, for 12 minutes", KioskHealthDisplay.LastOutage(snapshot, Now));
    }

    [TestMethod]
    public void LastOutage_WithoutAnEnd_SaysItWasStillGoing()
    {
        ClientHealthSnapshot snapshot = Snapshot(s => s.LastOfflineStartedUtc = Now.AddMinutes(-20));

        Assert.AreEqual("20 minutes ago, still going at its last report", KioskHealthDisplay.LastOutage(snapshot, Now));
        Assert.IsNull(KioskHealthDisplay.LastOutage(Snapshot(_ => { }), Now));
    }

    [TestMethod]
    public void ShouldBePlayingOffline_OnlyForASilentKioskThatWasReady()
    {
        ClientHealthSnapshot ready = Snapshot(s => s.OfflineFallbackReady = true);
        ClientHealthSnapshot notReady = Snapshot(s => s.OfflineFallbackReady = false);

        Assert.IsTrue(KioskHealthDisplay.ShouldBePlayingOffline(new KioskHealthView { Status = KioskHealthStatus.Offline, Snapshot = ready }));
        Assert.IsTrue(KioskHealthDisplay.ShouldBePlayingOffline(new KioskHealthView { Status = KioskHealthStatus.Unresponsive, Snapshot = ready }));
        Assert.IsFalse(KioskHealthDisplay.ShouldBePlayingOffline(new KioskHealthView { Status = KioskHealthStatus.Healthy, Snapshot = ready }));
        Assert.IsFalse(KioskHealthDisplay.ShouldBePlayingOffline(new KioskHealthView { Status = KioskHealthStatus.Offline, Snapshot = notReady }));
    }
}
