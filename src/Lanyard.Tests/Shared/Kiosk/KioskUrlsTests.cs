using Lanyard.Shared.Kiosk;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Kiosk;

// Security Issue 9.10.
[TestClass]
public class KioskUrlsTests
{
    [TestMethod]
    public void WithoutQuery_DropsTheTokenBeforeLogging()
    {
        Assert.AreEqual("https://lanyard.example/kiosk/a/b", KioskUrls.WithoutQuery("https://lanyard.example/kiosk/a/b?display=0&token=secret"));
        Assert.AreEqual("https://lanyard.example/video-publisher/a", KioskUrls.WithoutQuery("https://lanyard.example/video-publisher/a?token=secret#x"));
        Assert.AreEqual("https://lanyard.example/x", KioskUrls.WithoutQuery("https://lanyard.example/x"));
        Assert.AreEqual(string.Empty, KioskUrls.WithoutQuery(null));
    }

    [TestMethod]
    [DataRow("https://lanyard.example/kiosk/a/b", true)]
    [DataRow("https://LANYARD.example:443/other", true)]
    [DataRow("https://evil.example/", false)]
    [DataRow("http://lanyard.example/kiosk/a/b", false)]
    [DataRow("https://lanyard.example:8443/", false)]
    public void CameraAndMicrophone_OnlyForThePageTheWindowOpened(string requestUri, bool allowed)
    {
        Assert.AreEqual(allowed, KioskUrls.IsSameOrigin(requestUri, "https://lanyard.example/kiosk/a/b?token=x"));
    }
}
