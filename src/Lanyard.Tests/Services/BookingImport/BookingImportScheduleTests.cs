using Lanyard.Application.Services.BookingImport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.BookingImport;

[TestClass]
public class BookingImportScheduleTests
{
    private static DateTime Local(int hour) => new(2026, 6, 20, hour, 0, 0, DateTimeKind.Unspecified);

    [TestMethod]
    public void IsWithinActiveHours_IsHalfOpenWindow()
    {
        Assert.IsTrue(BookingImportSchedule.IsWithinActiveHours(7, 7, 23));
        Assert.IsTrue(BookingImportSchedule.IsWithinActiveHours(22, 7, 23));
        Assert.IsFalse(BookingImportSchedule.IsWithinActiveHours(23, 7, 23));
        Assert.IsFalse(BookingImportSchedule.IsWithinActiveHours(3, 7, 23));
    }

    [TestMethod]
    public void IsDue_OutsideActiveHours_NeverRuns()
    {
        // 02:00 local, never run before: still not due because it's overnight.
        Assert.IsFalse(BookingImportSchedule.IsDue(DateTime.UtcNow, Local(2), null, 1, 7, 23));
    }

    [TestMethod]
    public void IsDue_FirstRunInsideHours_RunsImmediately()
    {
        Assert.IsTrue(BookingImportSchedule.IsDue(DateTime.UtcNow, Local(9), null, 1, 7, 23));
    }

    [TestMethod]
    public void IsDue_WaitsForTheIntervalToElapse()
    {
        DateTime now = new(2026, 6, 20, 9, 0, 0, DateTimeKind.Utc);

        // Last ran 30 minutes ago, hourly: not yet.
        Assert.IsFalse(BookingImportSchedule.IsDue(now, Local(9), now.AddMinutes(-30), 1, 7, 23));

        // Last ran 61 minutes ago, hourly: due.
        Assert.IsTrue(BookingImportSchedule.IsDue(now, Local(9), now.AddMinutes(-61), 1, 7, 23));
    }

    [TestMethod]
    public void IsDue_HonoursACustomFrequency()
    {
        DateTime now = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

        // Every 3 hours: two hours on is not enough, four hours is.
        Assert.IsFalse(BookingImportSchedule.IsDue(now, Local(12), now.AddHours(-2), 3, 7, 23));
        Assert.IsTrue(BookingImportSchedule.IsDue(now, Local(12), now.AddHours(-4), 3, 7, 23));
    }
}
