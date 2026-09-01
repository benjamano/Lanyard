using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Models;

[TestClass]
public class AutomationScheduleDaysTests
{
    [TestMethod]
    public void Serialize_ProducesASortedDedupedCsv()
    {
        string csv = AutomationScheduleDays.Serialize([DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Monday]);

        Assert.AreEqual("1,5", csv);
    }

    [TestMethod]
    public void Serialize_OfAnEmptySetProducesAnEmptyString()
    {
        string csv = AutomationScheduleDays.Serialize([]);

        Assert.AreEqual(string.Empty, csv);
    }

    [TestMethod]
    public void TryParse_RoundTripsASerializedValue()
    {
        string csv = AutomationScheduleDays.Serialize([DayOfWeek.Wednesday, DayOfWeek.Sunday]);

        Assert.IsTrue(AutomationScheduleDays.TryParse(csv, out List<DayOfWeek> days));
        CollectionAssert.AreEquivalent(new[] { DayOfWeek.Sunday, DayOfWeek.Wednesday }, days);
    }

    [TestMethod]
    public void TryParse_TreatsNullAsEveryDay()
    {
        Assert.IsTrue(AutomationScheduleDays.TryParse(null, out List<DayOfWeek> days));
        Assert.HasCount(7, days);
    }

    [TestMethod]
    public void TryParse_TreatsEmptyAsEveryDay()
    {
        Assert.IsTrue(AutomationScheduleDays.TryParse(string.Empty, out List<DayOfWeek> days));
        Assert.HasCount(7, days);
    }

    [TestMethod]
    public void TryParse_TolerateWhitespaceAroundEntries()
    {
        Assert.IsTrue(AutomationScheduleDays.TryParse(" 1 , 2 ,3 ", out List<DayOfWeek> days));
        CollectionAssert.AreEquivalent(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday }, days);
    }

    [TestMethod]
    public void TryParse_RejectsAnOutOfRangeValue()
    {
        Assert.IsFalse(AutomationScheduleDays.TryParse("8", out _));
    }

    [TestMethod]
    public void TryParse_RejectsANonNumericValue()
    {
        Assert.IsFalse(AutomationScheduleDays.TryParse("Monday", out _));
    }

    [TestMethod]
    public void TryParse_RejectsACsvWithNoUsableDayTokens()
    {
        // A non-blank CSV with nothing but separators must not silently parse to the same empty
        // list "every day" uses internally - that would validate successfully yet Matches() would
        // then reject every day, leaving a rule that can never fire.
        Assert.IsFalse(AutomationScheduleDays.TryParse(",", out _));
        Assert.IsFalse(AutomationScheduleDays.TryParse(" , , ", out _));
    }

    [TestMethod]
    public void Matches_ReturnsFalseForAMalformedValueRatherThanEveryDay()
    {
        Assert.IsFalse(AutomationScheduleDays.Matches("Monday", DayOfWeek.Monday));
    }
}
