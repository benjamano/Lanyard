using Lanyard.App.Services.CircuitShutdown;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace Lanyard.Tests.Services.CircuitShutdown;

[TestClass]
public class CircuitShutdownCoordinatorTests
{
    private static CircuitShutdownCoordinator Create(TimeSpan? drainTimeout = null) =>
        new(Mock.Of<IHostApplicationLifetime>(), Options.Create(new CircuitShutdownOptions { DrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(5) }),
            NullLogger<CircuitShutdownCoordinator>.Instance);

    [TestMethod]
    public async Task Drain_WithNoCircuits_ReturnsStraightAway()
    {
        CircuitShutdownCoordinator coordinator = Create();

        await coordinator.DrainAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(coordinator.IsStopping);
    }

    [TestMethod]
    public async Task Drain_AsksEveryOpenCircuitToPause_AndWaitsForThemToDisconnect()
    {
        CircuitShutdownCoordinator coordinator = Create();
        List<string> asked = [];
        foreach (string id in new[] { "a", "b" })
        {
            coordinator.Register(id, _ =>
            {
                lock (asked) { asked.Add(id); }
                return Task.FromResult(true);
            });
        }

        Task drain = coordinator.DrainAsync(CancellationToken.None);
        await Task.Delay(50);

        CollectionAssert.AreEquivalent(new[] { "a", "b" }, asked);
        Assert.IsFalse(drain.IsCompleted, "Drain should wait until the browsers have actually paused");

        coordinator.Unregister("a");
        coordinator.Unregister("b");

        await drain.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, coordinator.OpenCircuitCount);
    }

    [TestMethod]
    public async Task Drain_GivesUpAfterTheTimeout_WhenACircuitNeverPauses()
    {
        CircuitShutdownCoordinator coordinator = Create(TimeSpan.FromMilliseconds(100));
        coordinator.Register("stuck", _ => Task.FromResult(true));

        await coordinator.DrainAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, coordinator.OpenCircuitCount);
    }

    [TestMethod]
    public async Task Drain_DoesNotWaitOnCircuitsThatRefuseOrFail()
    {
        CircuitShutdownCoordinator coordinator = Create(TimeSpan.FromSeconds(10));
        coordinator.Register("refused", _ => Task.FromResult(false));
        coordinator.Register("broken", _ => throw new InvalidOperationException("gone"));

        await coordinator.DrainAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, coordinator.OpenCircuitCount);
    }

    [TestMethod]
    public async Task Register_WhileStopping_AsksTheNewCircuitToPauseToo()
    {
        CircuitShutdownCoordinator coordinator = Create(TimeSpan.FromMilliseconds(200));
        coordinator.Register("first", _ => Task.FromResult(true));
        Task drain = coordinator.DrainAsync(CancellationToken.None);

        TaskCompletionSource askedLate = new();
        coordinator.Register("late", _ =>
        {
            askedLate.TrySetResult();
            return Task.FromResult(true);
        });

        await askedLate.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Drain_OnlyRunsOnce()
    {
        CircuitShutdownCoordinator coordinator = Create(TimeSpan.FromMilliseconds(100));
        int asks = 0;
        coordinator.Register("a", _ => { Interlocked.Increment(ref asks); return Task.FromResult(false); });

        await coordinator.DrainAsync(CancellationToken.None);
        await coordinator.DrainAsync(CancellationToken.None);

        Assert.AreEqual(1, asks);
    }

    [TestMethod]
    public void Register_BeforeStopping_DoesNotPause()
    {
        CircuitShutdownCoordinator coordinator = Create();
        bool asked = false;

        coordinator.Register("a", _ => { asked = true; return Task.FromResult(true); });

        Assert.IsFalse(asked);
        Assert.AreEqual(1, coordinator.OpenCircuitCount);
    }
}
