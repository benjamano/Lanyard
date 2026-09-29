using Lanyard.Infrastructure.Enum;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class RotaTimeTests
{
    [TestMethod]
    [DataRow(2026, 10, 4, 2026, 9, 28)]  // Sunday -> previous Monday
    [DataRow(2026, 10, 5, 2026, 10, 5)]  // Monday -> itself
    [DataRow(2026, 10, 7, 2026, 10, 5)]  // Wednesday
    [DataRow(2027, 1, 1, 2026, 12, 28)]  // across a year boundary
    public void GetWeekStart_ReturnsMonday(int y, int m, int d, int ey, int em, int ed)
    {
        Assert.AreEqual(new DateOnly(ey, em, ed), RotaTime.GetWeekStart(new DateOnly(y, m, d)));
    }

    [TestMethod]
    public void ToUtc_SummerTimeIsOneHourBehindWallClock()
    {
        DateTime utc = RotaTime.ToUtc(new DateOnly(2026, 10, 5), new TimeOnly(9, 0));

        Assert.AreEqual(new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc), utc);
        Assert.AreEqual(DateTimeKind.Utc, utc.Kind);
    }

    [TestMethod]
    public void ToUtc_WinterTimeMatchesUtc()
    {
        Assert.AreEqual(new DateTime(2026, 12, 7, 9, 0, 0, DateTimeKind.Utc), RotaTime.ToUtc(new DateOnly(2026, 12, 7), new TimeOnly(9, 0)));
    }

    [TestMethod]
    public void ToUtc_WallClockTimeInSpringForwardGapIsMovedPastIt()
    {
        // 28 March 2027: clocks jump 01:00 -> 02:00, so 01:30 doesn't exist locally.
        DateTime utc = RotaTime.ToUtc(new DateOnly(2027, 3, 28), new TimeOnly(1, 30));

        Assert.AreEqual(new DateTime(2027, 3, 28, 1, 30, 0, DateTimeKind.Utc), utc);
        Assert.AreEqual(new TimeOnly(2, 30), RotaTime.LocalTime(utc));
    }

    [TestMethod]
    public void RoundTrip_PreservesWallClockAcrossClockChange()
    {
        DateOnly beforeChange = new(2026, 10, 19);
        DateOnly afterChange = new(2026, 11, 2);

        Assert.AreEqual(new TimeOnly(9, 0), RotaTime.LocalTime(RotaTime.ToUtc(beforeChange, new TimeOnly(9, 0))));
        Assert.AreEqual(new TimeOnly(9, 0), RotaTime.LocalTime(RotaTime.ToUtc(afterChange, new TimeOnly(9, 0))));
    }

    [TestMethod]
    public void LocalDate_LateUtcEveningInSummerIsNextLocalDay()
    {
        DateTime utc = new(2026, 10, 5, 23, 30, 0, DateTimeKind.Utc);

        Assert.AreEqual(new DateOnly(2026, 10, 6), RotaTime.LocalDate(utc));
    }
}
