using Lanyard.Application.Services;
using Lanyard.Shared.Enum;
using Lanyard.Shared.Kiosk;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class KioskSelfTestCatalogueTests
{
    [TestMethod]
    public void EveryKnownCheck_HasATitle_AndEveryGroupIsNamed()
    {
        foreach (string key in SelfTestCheckKeys.All)
        {
            Assert.DoesNotStartWith("Unrecognised", KioskSelfTestCatalogue.Title(key), $"{key} has no title");
            Assert.AreNotEqual("Other checks", KioskSelfTestCatalogue.GroupTitle(SelfTestCheckKeys.GroupOf(key)), $"{key}'s group has no title");
        }
    }

    [TestMethod]
    public void EverySpecificHint_IsForAKnownCheckAndCode()
    {
        HashSet<string> codes = typeof(SelfTestCodes).GetFields()
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        foreach ((string key, string code) in KioskSelfTestCatalogue.KnownHints)
        {
            Assert.IsTrue(SelfTestCheckKeys.IsKnown(key), $"Hint for unknown check {key}");
            Assert.Contains(code, codes, $"Hint for unknown code {code}");
        }
    }

    [TestMethod]
    public void Hint_NothingToDoForPassesAndUnconfiguredHardware()
    {
        Assert.IsNull(KioskSelfTestCatalogue.Hint(SelfTestCheckKeys.DmxPort, SelfTestOutcome.Pass, null));
        Assert.IsNull(KioskSelfTestCatalogue.Hint(SelfTestCheckKeys.DmxDriver, SelfTestOutcome.NotConfigured, null));
        Assert.IsNull(KioskSelfTestCatalogue.Hint(SelfTestCheckKeys.CaptureTraffic, SelfTestOutcome.Skipped, null), "A skip with no code needs nothing doing");
    }

    [TestMethod]
    public void Hint_SpecificWhenKnown_ElseAFallback()
    {
        StringAssert.Contains(KioskSelfTestCatalogue.Hint(SelfTestCheckKeys.DmxDriver, SelfTestOutcome.Fail, SelfTestCodes.DriverMissing), "FTDI");
        StringAssert.Contains(KioskSelfTestCatalogue.Hint(SelfTestCheckKeys.AudioTone, SelfTestOutcome.Skipped, SelfTestCodes.MusicPlaying), "Pause the music");

        // Codes any check can carry.
        StringAssert.Contains(KioskSelfTestCatalogue.Hint(SelfTestCheckKeys.WebView2Runtime, SelfTestOutcome.Fail, SelfTestCodes.TimedOut), "took too long");
        StringAssert.Contains(KioskSelfTestCatalogue.Hint(SelfTestCheckKeys.AudioDevice, SelfTestOutcome.Fail, SelfTestCodes.Exception), "Restart the kiosk");

        // A failure this server has no specific advice for still gets something to try.
        StringAssert.Contains(KioskSelfTestCatalogue.Hint("printer.paper", SelfTestOutcome.Fail, "Jammed"), "Restart the kiosk");
    }

    [TestMethod]
    public void GroupOrder_FixedThenUnknownLast()
    {
        Assert.IsLessThan(KioskSelfTestCatalogue.GroupOrder(SelfTestCheckKeys.GroupWebView2), KioskSelfTestCatalogue.GroupOrder(SelfTestCheckKeys.GroupAudio));
        Assert.AreEqual(int.MaxValue, KioskSelfTestCatalogue.GroupOrder("printer"));
    }
}
