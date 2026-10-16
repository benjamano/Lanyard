using Lanyard.Shared.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Diagnostics;

[TestClass]
public class ScreenshotSizingTests
{
    [TestMethod]
    public void Attempts_StartAtTheRequestedWidthAndKeepTheShape()
    {
        (int width, int height, int quality) = ScreenshotSizing.Attempts(1920, 1080, 1280).First();

        Assert.AreEqual(1280, width);
        Assert.AreEqual(720, height);
        Assert.AreEqual(75, quality);
    }

    [TestMethod]
    public void Attempts_NeverScaleASmallScreenUp()
    {
        (int width, int height, _) = ScreenshotSizing.Attempts(800, 600, 1280).First();

        Assert.AreEqual(800, width);
        Assert.AreEqual(600, height);
    }

    [TestMethod]
    public void Attempts_LowerTheQualityBeforeShrinking()
    {
        List<(int Width, int Height, int Quality)> attempts = ScreenshotSizing.Attempts(1920, 1080, 1280).ToList();

        Assert.AreEqual((1280, 720, 75), attempts[0]);
        Assert.AreEqual((1280, 720, 60), attempts[1]);
        Assert.AreEqual(960, attempts[2].Width);
    }

    [TestMethod]
    public void Attempts_GetSmallerEachRoundAndStopAtTheMinimumWidth()
    {
        List<(int Width, int Height, int Quality)> attempts = ScreenshotSizing.Attempts(3840, 2160, 1920).ToList();

        for (int i = 1; i < attempts.Count; i++)
        {
            Assert.IsLessThanOrEqualTo(attempts[i - 1].Width, attempts[i].Width);
        }

        Assert.AreEqual(ScreenshotSizing.MinWidth, attempts[^1].Width);
        Assert.AreEqual(180, attempts[^1].Height);
    }

    [TestMethod]
    public void Attempts_AreEmptyForAnEmptyCapture()
    {
        Assert.IsFalse(ScreenshotSizing.Attempts(0, 1080, 1280).Any());
    }

    [TestMethod]
    public void Clamps_HoldTheKiosksOwnLimitsWhateverTheServerAsks()
    {
        // The JPEG travels as base64 inside a JSON hub message the server caps at 256 KB.
        Assert.AreEqual(ScreenshotSizing.MaxBytesLimit, ScreenshotSizing.ClampMaxBytes(10 * 1024 * 1024));
        Assert.IsLessThan(256 * 1024, ScreenshotSizing.MaxBytesLimit * 4 / 3 + 4 * 1024);
        Assert.AreEqual(ScreenshotSizing.MaxWidthLimit, ScreenshotSizing.ClampMaxWidth(10_000));
        Assert.AreEqual(ScreenshotSizing.MaxWidthLimit, ScreenshotSizing.ClampMaxWidth(0));
        Assert.AreEqual(ScreenshotSizing.MinWidth, ScreenshotSizing.ClampMaxWidth(10));
    }
}
