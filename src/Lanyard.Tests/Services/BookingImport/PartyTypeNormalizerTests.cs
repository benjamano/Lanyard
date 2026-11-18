using Lanyard.Application.Services.BookingImport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.BookingImport;

[TestClass]
public class PartyTypeNormalizerTests
{
    [DataTestMethod]
    // Trailing full stops and whitespace.
    [DataRow("Play Party.", "Play Party")]
    [DataRow("  Play   Party  ", "Play Party")]
    // Case variants collapse to one.
    [DataRow("play party", "Play Party")]
    [DataRow("PLAY PARTY", "Play Party")]
    [DataRow("Play and Laser party.", "Play and Laser Party")]
    // "and"/"of"/"the" stay lower (except first word).
    [DataRow("adventure play party", "Adventure Play Party")]
    // Trailing time ranges are stripped, in several formats.
    [DataRow("Play & Inflatable Exclusive Hire 6.30pm - 8.30pm", "Play & Inflatable Exclusive Hire")]
    [DataRow("Adventure Play Party 11:30AM - 01:30PM", "Adventure Play Party")]
    [DataRow("Adventure Play Party 4.30pm to 6.30pm", "Adventure Play Party")]
    public void Normalize_CleansCosmeticDifferences(string raw, string expected)
    {
        Assert.AreEqual(expected, PartyTypeNormalizer.Normalize(raw));
    }

    [TestMethod]
    public void Normalize_MergesConfirmedSynonymToCanonicalName()
    {
        // Ben confirmed these are the same product; both forms (and their time/case variants) merge.
        Assert.AreEqual("Play & Inflatable Exclusive Hire", PartyTypeNormalizer.Normalize("Play and Inflatable private hire"));
        Assert.AreEqual("Play & Inflatable Exclusive Hire", PartyTypeNormalizer.Normalize("play and inflatable private hire 6.30pm - 8.30pm"));
        Assert.AreEqual("Play & Inflatable Exclusive Hire", PartyTypeNormalizer.Normalize("Play & Inflatable Exclusive Hire 6.30pm - 8.30pm"));
    }

    [TestMethod]
    public void Normalize_KeepsDistinctProductsSeparate()
    {
        // The laser hire is a different product and must not fold into the plain exclusive hire.
        string laser = PartyTypeNormalizer.Normalize("Lazer, Play and Inflatable Private Hire");
        Assert.AreNotEqual("Play & Inflatable Exclusive Hire", laser);
    }

    [TestMethod]
    public void Normalize_KeepsATimeOnlyNameRatherThanBlankingIt()
    {
        Assert.AreEqual("4.30pm to 6.30pm", PartyTypeNormalizer.Normalize("4.30pm to 6.30pm"));
    }

    [TestMethod]
    public void Normalize_EmptyStaysEmpty()
    {
        Assert.AreEqual(string.Empty, PartyTypeNormalizer.Normalize(""));
        Assert.AreEqual(string.Empty, PartyTypeNormalizer.Normalize("   "));
        Assert.AreEqual(string.Empty, PartyTypeNormalizer.Normalize(null));
    }
}
