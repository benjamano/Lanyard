using Lanyard.Application.Services;
using Lanyard.Shared.DTO;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class ClientDiagnosticsStoreTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static ClientLogTailDTO Tail(string message)
    {
        return new ClientLogTailDTO { Lines = [new ClientLogLineDTO { Message = message }] };
    }

    [TestMethod]
    public void GetLatestLogTail_ReturnsTheMostRecentCapture()
    {
        ClientDiagnosticsStore store = new(new TestClock(Now));
        Guid clientId = Guid.NewGuid();

        store.SaveLogTail(clientId, Tail("first"), "Ben");
        store.SaveLogTail(clientId, Tail("second"), "Alex");

        ClientLogTailCapture? latest = store.GetLatestLogTail(clientId);

        Assert.IsNotNull(latest);
        Assert.AreEqual("second", latest.LogTail.Lines.Single().Message);
        Assert.AreEqual("Alex", latest.RequestedBy);
        Assert.AreEqual(Now + ClientDiagnosticsStore.Retention, latest.ExpiresAtUtc);
    }

    [TestMethod]
    public void GetLatestLogTail_ForgetsCapturesAfterTheRetentionPeriod()
    {
        TestClock clock = new(Now);
        ClientDiagnosticsStore store = new(clock);
        Guid clientId = Guid.NewGuid();
        store.SaveLogTail(clientId, Tail("old"), "Ben");

        clock.Advance(ClientDiagnosticsStore.Retention - TimeSpan.FromSeconds(1));
        Assert.IsNotNull(store.GetLatestLogTail(clientId));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsNull(store.GetLatestLogTail(clientId));
    }

    [TestMethod]
    public void GetLatestLogTail_KeepsClientsSeparate()
    {
        ClientDiagnosticsStore store = new(new TestClock(Now));
        Guid first = Guid.NewGuid();
        store.SaveLogTail(first, Tail("first"), "Ben");

        Assert.IsNull(store.GetLatestLogTail(Guid.NewGuid()));
        Assert.IsNotNull(store.GetLatestLogTail(first));
    }

    [TestMethod]
    public void TryBeginLogTail_RefusesWhileOneIsInFlight()
    {
        TestClock clock = new(Now);
        ClientDiagnosticsStore store = new(clock);
        Guid clientId = Guid.NewGuid();

        Assert.IsTrue(store.TryBeginLogTail(clientId, out _));

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.IsFalse(store.TryBeginLogTail(clientId, out string? reason));
        StringAssert.Contains(reason, "already being fetched");
    }

    [TestMethod]
    public void TryBeginLogTail_AppliesACooldownAfterTheLastRequest()
    {
        TestClock clock = new(Now);
        ClientDiagnosticsStore store = new(clock);
        Guid clientId = Guid.NewGuid();

        Assert.IsTrue(store.TryBeginLogTail(clientId, out _));
        store.EndLogTail(clientId);

        Assert.IsFalse(store.TryBeginLogTail(clientId, out string? reason));
        StringAssert.Contains(reason, "a moment ago");

        clock.Advance(ClientDiagnosticsStore.LogTailCooldown);

        Assert.IsTrue(store.TryBeginLogTail(clientId, out _));
    }

    [TestMethod]
    public void TryBeginLogTail_DoesNotBlockOtherClients()
    {
        ClientDiagnosticsStore store = new(new TestClock(Now));

        Assert.IsTrue(store.TryBeginLogTail(Guid.NewGuid(), out _));
        Assert.IsTrue(store.TryBeginLogTail(Guid.NewGuid(), out _));
    }
}
