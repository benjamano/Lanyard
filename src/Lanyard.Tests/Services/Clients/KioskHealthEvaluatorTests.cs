using Lanyard.Application.Services.Clients;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class KioskHealthEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly KioskHealthOptions Options = new();

    private static ClientHealthSnapshot Snapshot(TimeSpan sinceLastBeat, Action<ClientHealthSnapshot>? configure = null)
    {
        ClientHealthSnapshot snapshot = new()
        {
            ClientId = Guid.NewGuid(),
            LastHeartbeatUtc = Now - sinceLastBeat,
            ClientVersion = "1.0.41",
            CpuPercent = 10,
            MemoryPercent = 40,
            SystemDriveFreeBytes = 100L * 1024 * 1024 * 1024,
            SystemDriveTotalBytes = 250L * 1024 * 1024 * 1024
        };

        configure?.Invoke(snapshot);

        return snapshot;
    }

    private static KioskHealthView Evaluate(ClientHealthSnapshot? snapshot, bool isConnected = true, DateTime? lastDisconnect = null, string? newest = null)
    {
        return KioskHealthEvaluator.Evaluate(snapshot, isConnected, lastDisconnect, Now, Options, newest);
    }

    [TestMethod]
    public void NoSnapshot_IsNoTelemetry()
    {
        Assert.AreEqual(KioskHealthStatus.NoTelemetry, Evaluate(null).Status);
    }

    [TestMethod]
    public void RecentBeat_IsHealthy()
    {
        Assert.AreEqual(KioskHealthStatus.Healthy, Evaluate(Snapshot(TimeSpan.FromSeconds(20))).Status);
    }

    [TestMethod]
    public void JustUnderThreeMissedBeats_IsStillHealthy()
    {
        Assert.AreEqual(KioskHealthStatus.Healthy, Evaluate(Snapshot(TimeSpan.FromSeconds(89))).Status);
    }

    [TestMethod]
    public void ThreeMissedBeats_IsUnresponsive_EvenWhileTheHubStillShowsAConnection()
    {
        // A hung kiosk keeps its socket open until SignalR times out; the heartbeat is what catches it.
        Assert.AreEqual(KioskHealthStatus.Unresponsive, Evaluate(Snapshot(TimeSpan.FromSeconds(90)), isConnected: true).Status);
    }

    [TestMethod]
    public void UnresponsiveForTheOfflineWindow_IsOffline()
    {
        Assert.AreEqual(KioskHealthStatus.Unresponsive, Evaluate(Snapshot(Options.OfflineAfter - TimeSpan.FromSeconds(1))).Status);
        Assert.AreEqual(KioskHealthStatus.Offline, Evaluate(Snapshot(Options.OfflineAfter)).Status);
    }

    [TestMethod]
    public void CleanDisconnectAfterTheLastBeat_IsDisconnected_NotOffline()
    {
        ClientHealthSnapshot snapshot = Snapshot(TimeSpan.FromHours(2));

        KioskHealthView view = Evaluate(snapshot, isConnected: false, lastDisconnect: snapshot.LastHeartbeatUtc.AddSeconds(5));

        Assert.AreEqual(KioskHealthStatus.Disconnected, view.Status);
    }

    [TestMethod]
    public void DisconnectBeforeTheLastBeat_IsIgnored()
    {
        // An old disconnect followed by a reconnect and fresh heartbeats.
        ClientHealthSnapshot snapshot = Snapshot(TimeSpan.FromSeconds(10));

        KioskHealthView view = Evaluate(snapshot, isConnected: false, lastDisconnect: snapshot.LastHeartbeatUtc.AddMinutes(-30));

        Assert.AreEqual(KioskHealthStatus.Healthy, view.Status);
    }

    [TestMethod]
    public void LowDiskByGb_IsDegraded()
    {
        KioskHealthView view = Evaluate(Snapshot(TimeSpan.Zero, s => s.SystemDriveFreeBytes = 2L * 1024 * 1024 * 1024));

        Assert.AreEqual(KioskHealthStatus.Degraded, view.Status);
        StringAssert.Contains(view.Reasons.Single(), "Low disk space");
    }

    [TestMethod]
    public void LowDiskByPercent_IsDegraded()
    {
        // 20 GB is above the GB floor but only 5% of a 400 GB drive.
        KioskHealthView view = Evaluate(Snapshot(TimeSpan.Zero, s =>
        {
            s.SystemDriveFreeBytes = 20L * 1024 * 1024 * 1024;
            s.SystemDriveTotalBytes = 400L * 1024 * 1024 * 1024;
        }));

        Assert.AreEqual(KioskHealthStatus.Degraded, view.Status);
    }

    [TestMethod]
    public void DmxConfiguredButNotOpen_IsDegraded()
    {
        KioskHealthView view = Evaluate(Snapshot(TimeSpan.Zero, s => { s.DmxConfigured = true; s.DmxOpen = false; }));

        CollectionAssert.Contains(view.Reasons.ToList(), "DMX device not open");
    }

    [TestMethod]
    public void DmxNotConfigured_IsNotAProblem()
    {
        Assert.AreEqual(KioskHealthStatus.Healthy, Evaluate(Snapshot(TimeSpan.Zero, s => s.DmxConfigured = false)).Status);
    }

    [TestMethod]
    public void MissingPreferredAudioDevice_IsDegraded_ButNoPreferenceIsNot()
    {
        Assert.AreEqual(KioskHealthStatus.Degraded, Evaluate(Snapshot(TimeSpan.Zero, s => s.PreferredAudioDevicePresent = false)).Status);
        Assert.AreEqual(KioskHealthStatus.Healthy, Evaluate(Snapshot(TimeSpan.Zero, s => s.PreferredAudioDevicePresent = null)).Status);
    }

    [TestMethod]
    public void CaptureConfiguredButStopped_IsDegraded()
    {
        KioskHealthView view = Evaluate(Snapshot(TimeSpan.Zero, s => { s.PacketCaptureConfigured = true; s.PacketCaptureRunning = false; }));

        CollectionAssert.Contains(view.Reasons.ToList(), "Zone scoreboard capture stopped");
    }

    [TestMethod]
    public void HighCpuAndMemory_AreBothReported()
    {
        KioskHealthView view = Evaluate(Snapshot(TimeSpan.Zero, s => { s.CpuPercent = 95; s.MemoryPercent = 92; }));

        Assert.AreEqual(2, view.Reasons.Count);
    }

    [TestMethod]
    public void DegradedReasons_AreNotReported_WhileUnresponsive()
    {
        // Stale metrics say nothing about the kiosk now.
        KioskHealthView view = Evaluate(Snapshot(TimeSpan.FromMinutes(3), s => s.CpuPercent = 99));

        Assert.AreEqual(KioskHealthStatus.Unresponsive, view.Status);
        Assert.AreEqual(0, view.Reasons.Count);
    }

    [TestMethod]
    public void OlderVersion_IsInformationOnly()
    {
        KioskHealthView view = Evaluate(Snapshot(TimeSpan.Zero, s => s.ClientVersion = "1.0.39"), newest: "1.0.41");

        Assert.AreEqual(KioskHealthStatus.Healthy, view.Status);
        Assert.AreEqual("1.0.41", view.NewerVersionSeen);
    }

    [TestMethod]
    public void Newest_ComparesVersionsNumerically_AndSkipsUnparseable()
    {
        Assert.AreEqual("1.0.10", KioskHealthEvaluator.Newest(["1.0.9", "1.0.10", null, "dev"]));
    }
}
