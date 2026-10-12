using Lanyard.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared;

[TestClass]
public class ClientVersionFormatTests
{
    [TestMethod]
    [DataRow("1.0.41", "1.0.41")]
    [DataRow(" 1.0.41 ", "1.0.41")]
    [DataRow("1.0.42-beta.1", "1.0.42-beta.1")]
    [DataRow("2.10.3+build.7", "2.10.3+build.7")]
    public void TryNormalize_AcceptsSemVer(string raw, string expected)
    {
        bool accepted = ClientVersionFormat.TryNormalize(raw, out string version);

        Assert.IsTrue(accepted);
        Assert.AreEqual(expected, version);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("Unknown Version")]
    [DataRow("1.0")]
    [DataRow("1.0.41<script>")]
    [DataRow("1.0.41-" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void TryNormalize_RejectsAnythingElse(string? raw)
    {
        bool accepted = ClientVersionFormat.TryNormalize(raw, out string version);

        Assert.IsFalse(accepted);
        Assert.AreEqual(string.Empty, version);
    }

    [TestMethod]
    public void Compare_OrdersNumericallyNotAlphabetically()
    {
        Assert.IsGreaterThan(0, ClientVersionFormat.Compare("1.0.10", "1.0.9"));
        Assert.IsLessThan(0, ClientVersionFormat.Compare("1.0.9", "1.1.0"));
        Assert.AreEqual(0, ClientVersionFormat.Compare("1.0.41", "1.0.41+build.2"));
    }

    [TestMethod]
    public void Compare_RanksReleaseAbovePrereleaseOfSameNumber()
    {
        Assert.IsGreaterThan(0, ClientVersionFormat.Compare("1.0.42", "1.0.42-beta.1"));
        Assert.IsGreaterThan(0, ClientVersionFormat.Compare("1.0.42-beta.2", "1.0.42-beta.1"));
        Assert.IsGreaterThan(0, ClientVersionFormat.Compare("1.0.42-beta.1", "1.0.41"));
    }
}
