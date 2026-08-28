using Lanyard.Infrastructure.Enum;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.GameResults;

/// <summary>
/// ToUtcLowerBound is deliberately a pure function of the supplied local "now" rather than reading
/// the clock, so these boundaries can be asserted at fixed instants without sleeping.
/// </summary>
[TestClass]
public class HallOfFamePeriodExtensionsTests
{
    // A Wednesday, mid-afternoon.
    private static readonly DateTime LocalNow = new(2026, 8, 26, 15, 30, 0, DateTimeKind.Local);

    private static DateTime ExpectedUtc(DateTime localStart)
    {
        return DateTime.SpecifyKind(localStart, DateTimeKind.Local).ToUniversalTime();
    }

    [TestMethod]
    public void ToUtcLowerBound_Today_ReturnsLocalMidnight()
    {
        DateTime? result = HallOfFamePeriod.Today.ToUtcLowerBound(LocalNow);

        Assert.IsNotNull(result);
        Assert.AreEqual(ExpectedUtc(new DateTime(2026, 8, 26, 0, 0, 0)), result.Value);
    }

    [TestMethod]
    public void ToUtcLowerBound_ThisWeek_ReturnsMondayOfTheCurrentWeek()
    {
        DateTime? result = HallOfFamePeriod.ThisWeek.ToUtcLowerBound(LocalNow);

        Assert.IsNotNull(result);
        Assert.AreEqual(ExpectedUtc(new DateTime(2026, 8, 24, 0, 0, 0)), result.Value);
    }

    [TestMethod]
    public void ToUtcLowerBound_ThisWeek_TreatsSundayAsTheEndOfTheWeekNotTheStart()
    {
        // Sunday 30 August 2026 - the case a naive DayOfWeek subtraction gets wrong, because
        // DayOfWeek.Sunday is 0 and would otherwise resolve to the week that is about to start.
        DateTime sunday = new(2026, 8, 30, 10, 0, 0, DateTimeKind.Local);

        DateTime? result = HallOfFamePeriod.ThisWeek.ToUtcLowerBound(sunday);

        Assert.IsNotNull(result);
        Assert.AreEqual(ExpectedUtc(new DateTime(2026, 8, 24, 0, 0, 0)), result.Value);
    }

    [TestMethod]
    public void ToUtcLowerBound_ThisWeek_ReturnsTodayWhenItIsMonday()
    {
        DateTime monday = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Local);

        DateTime? result = HallOfFamePeriod.ThisWeek.ToUtcLowerBound(monday);

        Assert.IsNotNull(result);
        Assert.AreEqual(ExpectedUtc(new DateTime(2026, 8, 24, 0, 0, 0)), result.Value);
    }

    [TestMethod]
    public void ToUtcLowerBound_ThisMonth_ReturnsFirstOfTheMonth()
    {
        DateTime? result = HallOfFamePeriod.ThisMonth.ToUtcLowerBound(LocalNow);

        Assert.IsNotNull(result);
        Assert.AreEqual(ExpectedUtc(new DateTime(2026, 8, 1, 0, 0, 0)), result.Value);
    }

    [TestMethod]
    public void ToUtcLowerBound_AllTime_ReturnsNull()
    {
        Assert.IsNull(HallOfFamePeriod.AllTime.ToUtcLowerBound(LocalNow));
    }

    [TestMethod]
    public void ToUtcLowerBound_ReturnsAUtcKindedInstant()
    {
        // The query compares against PlayedAtUtc, so a Local-kinded bound would silently skew
        // every window by the server's offset.
        DateTime? result = HallOfFamePeriod.Today.ToUtcLowerBound(LocalNow);

        Assert.IsNotNull(result);
        Assert.AreEqual(DateTimeKind.Utc, result.Value.Kind);
    }
}
