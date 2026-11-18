using Lanyard.App.Components.Clients;
using Lanyard.Application.Services.Clients;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Components.Clients;

[TestClass]
public class ClientsPageRowsTests
{
    private static readonly Guid KioskId = Guid.NewGuid();

    private static ClientConnectedWithCapabilitiesDTO Kiosk(Action<ClientConnectedWithCapabilitiesDTO>? configure = null)
    {
        ClientConnectedWithCapabilitiesDTO kiosk = new()
        {
            Id = KioskId,
            Name = "Arena kiosk",
            MostRecentIpAddress = "10.0.0.5",
            ReportedClientVersion = "1.0.41",
            LastLogin = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc),
            IsCurrentlyConnected = true
        };

        configure?.Invoke(kiosk);
        return kiosk;
    }

    private static Dictionary<Guid, KioskHealthView> Health(KioskHealthStatus status, TimeSpan? sinceLastHeartbeat = null) => new()
    {
        [KioskId] = new KioskHealthView
        {
            Status = status,
            Snapshot = new ClientHealthSnapshot { ClientId = KioskId },
            SinceLastHeartbeat = sinceLastHeartbeat
        }
    };

    private static Dictionary<Guid, ClientUpdateTargetDTO> Targets(string? target = "1.0.41") => new()
    {
        [KioskId] = new ClientUpdateTargetDTO { ClientId = KioskId, Ring = ClientUpdateRing.Stable, TargetVersion = target, ReportedVersion = "1.0.41" }
    };

    private static bool Same(IReadOnlyList<ClientRowSignature> left, IReadOnlyList<ClientRowSignature> right) => left.SequenceEqual(right);

    [TestMethod]
    public void Build_TheSameDataTwice_IsEqual()
    {
        IReadOnlyList<ClientRowSignature> first = ClientsPageRows.Build([Kiosk()], Health(KioskHealthStatus.Healthy), Targets());
        IReadOnlyList<ClientRowSignature> second = ClientsPageRows.Build([Kiosk()], Health(KioskHealthStatus.Healthy), Targets());

        Assert.IsTrue(Same(first, second), "Freshly loaded but unchanged data must not cause a render.");
    }

    [TestMethod]
    public void Build_AnythingShownChanging_IsNotEqual()
    {
        IReadOnlyList<ClientRowSignature> baseline = ClientsPageRows.Build([Kiosk()], Health(KioskHealthStatus.Healthy), Targets());

        Assert.IsFalse(Same(baseline, ClientsPageRows.Build([Kiosk(x => x.Name = "Cafe kiosk")], Health(KioskHealthStatus.Healthy), Targets())), "Renamed");
        Assert.IsFalse(Same(baseline, ClientsPageRows.Build([Kiosk(x => x.IsCurrentlyConnected = false)], Health(KioskHealthStatus.Healthy), Targets())), "Disconnected");
        Assert.IsFalse(Same(baseline, ClientsPageRows.Build([Kiosk(x => x.DmxEnabled = true)], Health(KioskHealthStatus.Healthy), Targets())), "Gained DMX");
        Assert.IsFalse(Same(baseline, ClientsPageRows.Build([Kiosk()], Health(KioskHealthStatus.Degraded), Targets())), "Health changed");
        Assert.IsFalse(Same(baseline, ClientsPageRows.Build([Kiosk()], Health(KioskHealthStatus.Healthy), Targets("1.0.42"))), "New target");
        Assert.IsFalse(Same(baseline, ClientsPageRows.Build([Kiosk(), Kiosk(x => x.Id = Guid.NewGuid())], Health(KioskHealthStatus.Healthy), Targets())), "Kiosk added");
    }

    [TestMethod]
    public void Build_HeartbeatAgeOnlyCounts_WhenTheWordsShownChange()
    {
        IReadOnlyList<ClientRowSignature> at61 = ClientsPageRows.Build([Kiosk()], Health(KioskHealthStatus.Offline, TimeSpan.FromSeconds(61 * 60)), Targets());
        IReadOnlyList<ClientRowSignature> at62 = ClientsPageRows.Build([Kiosk()], Health(KioskHealthStatus.Offline, TimeSpan.FromSeconds(61 * 60 + 30)), Targets());
        IReadOnlyList<ClientRowSignature> later = ClientsPageRows.Build([Kiosk()], Health(KioskHealthStatus.Offline, TimeSpan.FromHours(3)), Targets());

        Assert.IsTrue(Same(at61, at62), "\"1 hour ago\" both times, so nothing to redraw.");
        Assert.IsFalse(Same(at61, later));
    }

    [TestMethod]
    public void Build_IgnoresTheOrderKiosksArriveIn()
    {
        ClientConnectedWithCapabilitiesDTO other = Kiosk(x => x.Id = Guid.NewGuid());

        Assert.IsTrue(Same(
            ClientsPageRows.Build([Kiosk(), other], Health(KioskHealthStatus.Healthy), Targets()),
            ClientsPageRows.Build([other, Kiosk()], Health(KioskHealthStatus.Healthy), Targets())));
    }

    [TestMethod]
    public void SameKiosks_IgnoresHealthAndTargets_ButNotTheKiosksThemselves()
    {
        IReadOnlyList<ClientRowSignature> baseline = ClientsPageRows.Build([Kiosk()], Health(KioskHealthStatus.Healthy), Targets());

        Assert.IsTrue(ClientsPageRows.SameKiosks(baseline, ClientsPageRows.Build([Kiosk()], Health(KioskHealthStatus.Offline, TimeSpan.FromHours(1)), Targets("1.0.42"))));
        Assert.IsFalse(ClientsPageRows.SameKiosks(baseline, ClientsPageRows.Build([Kiosk(x => x.IsCurrentlyConnected = false)], Health(KioskHealthStatus.Healthy), Targets())));
        Assert.IsFalse(ClientsPageRows.SameKiosks(baseline, ClientsPageRows.Build([Kiosk(x => x.Name = "Renamed")], Health(KioskHealthStatus.Healthy), Targets())));
    }

    [TestMethod]
    public void TargetInputs_ChangeWithTheRingPinOrReportedVersion_Only()
    {
        IReadOnlyList<(Guid, string?, ClientUpdateRing, string?)> baseline = ClientsPageRows.TargetInputs([Kiosk()]);

        Assert.IsTrue(baseline.SequenceEqual(ClientsPageRows.TargetInputs([Kiosk(x => x.Name = "Renamed")])));
        Assert.IsFalse(baseline.SequenceEqual(ClientsPageRows.TargetInputs([Kiosk(x => x.UpdateRing = ClientUpdateRing.Canary)])));
        Assert.IsFalse(baseline.SequenceEqual(ClientsPageRows.TargetInputs([Kiosk(x => x.ReportedClientVersion = "1.0.42")])));
    }
}
