using Lanyard.Application.Services.Clients;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class KioskFleetEventBusTests
{
    [TestMethod]
    public void Publish_ReachesEverySubscriber_WithTheCompany()
    {
        KioskFleetEventBus bus = new();
        List<int> first = [];
        List<int> second = [];
        bus.OnChanged += first.Add;
        bus.OnChanged += second.Add;

        bus.Publish(5);

        CollectionAssert.AreEqual(new[] { 5 }, first);
        CollectionAssert.AreEqual(new[] { 5 }, second);
    }

    [TestMethod]
    public void Publish_SkipsAnUnsubscribedPage()
    {
        KioskFleetEventBus bus = new();
        List<int> received = [];
        Action<int> handler = received.Add;

        bus.OnChanged += handler;
        bus.OnChanged -= handler;
        bus.Publish(5);

        Assert.AreEqual(0, received.Count);
    }

    [TestMethod]
    public void Publish_WithNoSubscribers_DoesNothing()
    {
        new KioskFleetEventBus().Publish(5);
    }
}
