using Lanyard.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared;

[TestClass]
public class ConnectRetryDelayTests
{
    [TestMethod]
    public void For_FirstFailure_UsesTheInitialDelay()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), ConnectRetryDelay.For(1, unauthorized: false));
    }

    [TestMethod]
    public void For_DoublesEachFailure()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), ConnectRetryDelay.For(2, unauthorized: false));
        Assert.AreEqual(TimeSpan.FromSeconds(20), ConnectRetryDelay.For(3, unauthorized: false));
        Assert.AreEqual(TimeSpan.FromSeconds(40), ConnectRetryDelay.For(4, unauthorized: false));
    }

    [TestMethod]
    public void For_NeverExceedsTheMax_EvenAfterManyFailures()
    {
        Assert.AreEqual(ConnectRetryDelay.Max, ConnectRetryDelay.For(5, unauthorized: false));
        Assert.AreEqual(ConnectRetryDelay.Max, ConnectRetryDelay.For(int.MaxValue, unauthorized: false));
    }

    [TestMethod]
    public void For_Unauthorized_RetriesSlowlyInsteadOfGivingUp()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(5), ConnectRetryDelay.For(1, unauthorized: true));
        Assert.AreEqual(TimeSpan.FromMinutes(5), ConnectRetryDelay.For(50, unauthorized: true));
    }
}
