using Lanyard.App.Components.Clients;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Components.Clients;

[TestClass]
public class KioskSessionFormatTests
{
    [TestMethod]
    [DataRow(0, "00:00:00")]
    [DataRow(59, "00:00:59")]
    [DataRow(3600, "01:00:00")]
    [DataRow(86399, "23:59:59")]
    public void Format_UnderADay_IsAPlainClock(int seconds, string expected)
    {
        Assert.AreEqual(expected, KioskSessionFormat.Format(TimeSpan.FromSeconds(seconds)));
    }

    [TestMethod]
    public void Format_PastADay_CountsTheDaysInsteadOfWrapping()
    {
        Assert.AreEqual("1d 01:00:00", KioskSessionFormat.Format(TimeSpan.FromHours(25)));
        Assert.AreEqual("3d 00:00:05", KioskSessionFormat.Format(TimeSpan.FromDays(3) + TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void Format_ClampsANegativeLengthToZero()
    {
        // A kiosk's LastLogin can be a moment ahead of the server's clock read.
        Assert.AreEqual("00:00:00", KioskSessionFormat.Format(TimeSpan.FromSeconds(-3)));
    }
}
