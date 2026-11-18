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
    // "and"/"of"/"the" stay lower (except first word); unaliased sample so this tests only casing.
    [DataRow("soft play and games party", "Soft Play and Games Party")]
    // Trailing time ranges are stripped, in several formats.
    [DataRow("Play & Inflatable Exclusive Hire 6.30pm - 8.30pm", "Play & Inflatable Exclusive Hire")]
    [DataRow("Soft Play Session 11:30AM - 01:30PM", "Soft Play Session")]
    [DataRow("Soft Play Session 4.30pm to 6.30pm", "Soft Play Session")]
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

    [DataTestMethod]
    // Confirmed groupings: stray wordings fold into the canonical type.
    [DataRow("Adventure Play Party", "Play Party")]
    [DataRow("Adventure Play and Laser Party", "Play and Laser Party")]
    [DataRow("Play and Inflatable Party", "Play & Inflatable Exclusive Hire")]
    [DataRow("Play and Inflatable private hire", "Play & Inflatable Exclusive Hire")]
    [DataRow("Lazer, Play and Inflatable Private Hire", "Play, Inflatable & Laser Tag Exclusive Hire")]
    [DataRow("Lazer, Play and Inflatable Party", "Play, Inflatable & Laser Tag Exclusive Hire")]
    [DataRow("1 Hour Lazer Tag Private Hire", "1 Hour Laser Tag Private Hire")]
    [DataRow("1 Hour Lazer Tag Private Hire & Soft Play", "1 Hour Laser Tag Private Hire")]
    public void Normalize_AppliesTheAliasGroups(string raw, string expected)
    {
        Assert.AreEqual(expected, PartyTypeNormalizer.Normalize(raw));
    }

    [TestMethod]
    public void Normalize_KeepsTheLaserHireSeparateFromThePlainHire()
    {
        // The laser hire is a different product from the plain play + inflatable hire.
        Assert.AreNotEqual(
            PartyTypeNormalizer.Normalize("Play & Inflatable Exclusive Hire"),
            PartyTypeNormalizer.Normalize("Lazer, Play and Inflatable Private Hire"));
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
