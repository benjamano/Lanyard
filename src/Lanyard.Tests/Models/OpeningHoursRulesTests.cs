using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Models;

[TestClass]
public class OpeningHoursRulesTests
{
    // Saturday 17 October 2026 and the Sunday after.
    private static readonly DateOnly Saturday = new(2026, 10, 17);
    private static readonly DateOnly Sunday = new(2026, 10, 18);

    private static LocationOpeningHours Day(DayOfWeek day, int openHour, int closeHour, bool isActive = true)
    {
        return new LocationOpeningHours
        {
            LocationId = 1,
            DayOfWeek = day,
            OpensAt = new TimeOnly(openHour, 0),
            ClosesAt = new TimeOnly(closeHour, 0),
            IsActive = isActive
        };
    }

    private static DateTime At(DateOnly date, int hour, int minute = 0) => date.ToDateTime(new TimeOnly(hour, minute));

    [TestMethod]
    public void IsOpen_IncludesOpeningAndExcludesClosingTime()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 10, 22)];

        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, At(Saturday, 9, 59)));
        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, At(Saturday, 10, 0)));
        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, At(Saturday, 21, 59)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, At(Saturday, 22, 0)));
    }

    [TestMethod]
    public void IsOpen_IsClosedOnADayWithNoRow()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 10, 22)];

        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, At(Sunday, 12, 0)));
    }

    [TestMethod]
    public void IsOpen_IgnoresRemovedWindows()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 10, 22, isActive: false)];

        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, At(Saturday, 12, 0)), "A soft-deleted window must not keep the venue open.");
    }

    [TestMethod]
    public void IsOpen_IsClosedBetweenTwoWindowsOnTheSameDay()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 11, 14), Day(DayOfWeek.Saturday, 17, 22)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, At(Saturday, 12, 0)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, At(Saturday, 15, 0)), "Shut between lunch and dinner.");
        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, At(Saturday, 18, 0)));
    }

    [TestMethod]
    public void IsOpen_CarriesLateHoursIntoTheNextMorning()
    {
        // Saturday 18:00 until 02:00 on Sunday; Sunday itself is closed.
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 18, 2)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, At(Saturday, 23, 30)));
        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, At(Sunday, 1, 59)), "Saturday's late hours run on into Sunday morning.");
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, At(Sunday, 2, 0)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, At(Saturday, 3, 0)), "Friday had no late hours, so early Saturday is closed.");
    }

    [TestMethod]
    public void IsOpen_WrapsFromSundayToMonday()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Sunday, 20, 1)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, At(new DateOnly(2026, 10, 19), 0, 30)));
    }

    [TestMethod]
    public void IsOpen_IgnoresARowWithEqualTimes()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 9, 9)];

        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, At(Saturday, 9, 0)));
        Assert.IsFalse(OpeningHoursRules.IsValid(new TimeOnly(9, 0), new TimeOnly(9, 0)));
    }

    [TestMethod]
    public void FindOverlap_SpotsOverlappingWindowsOnOneDay()
    {
        Assert.IsNotNull(OpeningHoursRules.FindOverlap([(DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(14, 0)), (DayOfWeek.Monday, new TimeOnly(13, 0), new TimeOnly(18, 0))]));
        Assert.IsNull(OpeningHoursRules.FindOverlap([(DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(14, 0)), (DayOfWeek.Monday, new TimeOnly(14, 0), new TimeOnly(18, 0))]), "Back-to-back windows don't overlap.");
        Assert.IsNull(OpeningHoursRules.FindOverlap([(DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(14, 0)), (DayOfWeek.Tuesday, new TimeOnly(10, 0), new TimeOnly(14, 0))]));
    }

    [TestMethod]
    public void FindOverlap_CountsLateHoursOnTheNextMorning()
    {
        // Saturday until 02:00 overlaps a Sunday window starting at 01:00.
        (DayOfWeek Day, TimeOnly A, TimeOnly B)? overlap = OpeningHoursRules.FindOverlap(
            [(DayOfWeek.Saturday, new TimeOnly(18, 0), new TimeOnly(2, 0)), (DayOfWeek.Sunday, new TimeOnly(1, 0), new TimeOnly(5, 0))]);

        Assert.IsNotNull(overlap);
        Assert.AreEqual(DayOfWeek.Sunday, overlap.Value.Day);
        Assert.IsNull(OpeningHoursRules.FindOverlap(
            [(DayOfWeek.Saturday, new TimeOnly(18, 0), new TimeOnly(2, 0)), (DayOfWeek.Sunday, new TimeOnly(10, 0), new TimeOnly(18, 0))]));
    }
}
