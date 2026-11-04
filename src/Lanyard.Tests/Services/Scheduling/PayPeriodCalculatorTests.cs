using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class PayPeriodCalculatorTests
{
    private static CompanySchedulingSettings Monthly(int startDay) =>
        new() { CompanyId = 1, PayPeriod = PayPeriodFrequency.Monthly, PayPeriodStartDay = startDay };

    private static CompanySchedulingSettings Every(PayPeriodFrequency frequency, DateOnly? anchor) =>
        new() { CompanyId = 1, PayPeriod = frequency, PayPeriodAnchorDate = anchor };

    private static void AssertPeriod(PayPeriod period, DateOnly from, DateOnly to)
    {
        Assert.AreEqual(from, period.From);
        Assert.AreEqual(to, period.To);
    }

    [TestMethod]
    public void Monthly_StartingOnThe1st_IsTheCalendarMonth()
    {
        AssertPeriod(PayPeriodCalculator.Containing(Monthly(1), new DateOnly(2026, 2, 14)), new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        AssertPeriod(PayPeriodCalculator.Containing(Monthly(1), new DateOnly(2028, 2, 29)), new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29));
    }

    [TestMethod]
    public void Monthly_WithACutOffDay_RunsFromThatDayToTheDayBefore()
    {
        CompanySchedulingSettings settings = Monthly(26);

        AssertPeriod(PayPeriodCalculator.Containing(settings, new DateOnly(2026, 9, 25)), new DateOnly(2026, 8, 26), new DateOnly(2026, 9, 25));
        AssertPeriod(PayPeriodCalculator.Containing(settings, new DateOnly(2026, 9, 26)), new DateOnly(2026, 9, 26), new DateOnly(2026, 10, 25));
    }

    [TestMethod]
    public void Monthly_WithACutOffDay_CrossesTheYearEnd()
    {
        AssertPeriod(PayPeriodCalculator.Containing(Monthly(26), new DateOnly(2027, 1, 10)), new DateOnly(2026, 12, 26), new DateOnly(2027, 1, 25));
    }

    [TestMethod]
    public void Previous_IsTheWholePeriodBefore()
    {
        AssertPeriod(PayPeriodCalculator.Previous(Monthly(26), new DateOnly(2026, 10, 13)), new DateOnly(2026, 8, 26), new DateOnly(2026, 9, 25));
        AssertPeriod(PayPeriodCalculator.Previous(Monthly(1), new DateOnly(2026, 3, 1)), new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
    }

    [TestMethod]
    [DataRow(PayPeriodFrequency.Weekly, 7)]
    [DataRow(PayPeriodFrequency.Fortnightly, 14)]
    [DataRow(PayPeriodFrequency.FourWeekly, 28)]
    public void WeeklyBased_CountsWholePeriodsFromTheAnchor(PayPeriodFrequency frequency, int length)
    {
        DateOnly anchor = new(2026, 1, 3); // a Saturday, to prove periods needn't start on a Monday
        CompanySchedulingSettings settings = Every(frequency, anchor);

        AssertPeriod(PayPeriodCalculator.Containing(settings, anchor), anchor, anchor.AddDays(length - 1));
        AssertPeriod(PayPeriodCalculator.Containing(settings, anchor.AddDays(length - 1)), anchor, anchor.AddDays(length - 1));
        AssertPeriod(PayPeriodCalculator.Containing(settings, anchor.AddDays(length * 10 + 3)), anchor.AddDays(length * 10), anchor.AddDays(length * 11 - 1));
    }

    [TestMethod]
    public void WeeklyBased_DateBeforeTheAnchor_FallsInTheRightPeriod()
    {
        DateOnly anchor = new(2026, 10, 5);
        CompanySchedulingSettings settings = Every(PayPeriodFrequency.Fortnightly, anchor);

        AssertPeriod(PayPeriodCalculator.Containing(settings, anchor.AddDays(-1)), anchor.AddDays(-14), anchor.AddDays(-1));
        AssertPeriod(PayPeriodCalculator.Containing(settings, anchor.AddDays(-15)), anchor.AddDays(-28), anchor.AddDays(-15));
    }

    [TestMethod]
    public void WeeklyBased_WithoutAnAnchor_FallsBackToMondayWeeksAndIsNotConfigured()
    {
        CompanySchedulingSettings settings = Every(PayPeriodFrequency.Fortnightly, null);

        AssertPeriod(PayPeriodCalculator.Containing(settings, new DateOnly(2026, 10, 15)), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 25));
        Assert.IsFalse(PayPeriodCalculator.IsConfigured(settings));
        Assert.IsTrue(PayPeriodCalculator.IsConfigured(Monthly(1)));
    }

    [TestMethod]
    public void Describe_NamesThePeriod()
    {
        Assert.AreEqual("Monthly (calendar month)", PayPeriodCalculator.Describe(Monthly(1)));
        Assert.AreEqual("Monthly, 26th to 25th", PayPeriodCalculator.Describe(Monthly(26)));
        Assert.AreEqual("Monthly, 2nd to 1st", PayPeriodCalculator.Describe(Monthly(2)));
        Assert.AreEqual("Every four weeks", PayPeriodCalculator.Describe(Every(PayPeriodFrequency.FourWeekly, new DateOnly(2026, 1, 5))));
    }

    [TestMethod]
    [DataRow(1, "1st")]
    [DataRow(11, "11th")]
    [DataRow(12, "12th")]
    [DataRow(13, "13th")]
    [DataRow(21, "21st")]
    [DataRow(22, "22nd")]
    [DataRow(23, "23rd")]
    [DataRow(25, "25th")]
    public void Ordinal_UsesEnglishSuffixes(int day, string expected)
    {
        Assert.AreEqual(expected, PayPeriodCalculator.Ordinal(day));
    }
}
