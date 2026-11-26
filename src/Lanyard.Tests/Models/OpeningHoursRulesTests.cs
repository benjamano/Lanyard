using Lanyard.Infrastructure.Enum;
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

        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 9, 59)));
        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 10, 0)));
        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 21, 59)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 22, 0)));
    }

    [TestMethod]
    public void IsOpen_IsClosedOnADayWithNoRow()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 10, 22)];

        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, [], At(Sunday, 12, 0)));
    }

    [TestMethod]
    public void IsOpen_IgnoresRemovedWindows()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 10, 22, isActive: false)];

        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 12, 0)), "A soft-deleted window must not keep the venue open.");
    }

    [TestMethod]
    public void IsOpen_IsClosedBetweenTwoWindowsOnTheSameDay()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 11, 14), Day(DayOfWeek.Saturday, 17, 22)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 12, 0)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 15, 0)), "Shut between lunch and dinner.");
        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 18, 0)));
    }

    [TestMethod]
    public void IsOpen_CarriesLateHoursIntoTheNextMorning()
    {
        // Saturday 18:00 until 02:00 on Sunday; Sunday itself is closed.
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 18, 2)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 23, 30)));
        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, [], At(Sunday, 1, 59)), "Saturday's late hours run on into Sunday morning.");
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, [], At(Sunday, 2, 0)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 3, 0)), "Friday had no late hours, so early Saturday is closed.");
    }

    [TestMethod]
    public void IsOpen_WrapsFromSundayToMonday()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Sunday, 20, 1)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, [], At(new DateOnly(2026, 10, 19), 0, 30)));
    }

    [TestMethod]
    public void IsOpen_IgnoresARowWithEqualTimes()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 9, 9)];

        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 9, 0)));
        Assert.IsFalse(OpeningHoursRules.IsValid(new TimeOnly(9, 0), new TimeOnly(9, 0)));
    }

    private static readonly DateOnly Monday = new(2026, 10, 19);

    private static RotaHoliday Holiday(RotaHolidayKind kind, DateOnly day, DayOfWeek? like = null, bool active = true, string name = "Holiday") => new()
    {
        CompanyId = 1,
        LocationId = 1,
        Name = name,
        Kind = kind,
        StartDate = day,
        EndDate = day,
        StaffLike = like,
        IsActive = active
    };

    [TestMethod]
    public void IsOpen_NoHolidays_MatchesTheWeeklyHours()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 10, 18)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, [], At(Saturday, 12)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, [], At(Sunday, 12)));
    }

    [TestMethod]
    public void IsOpen_ClosedDay_ShutAllDay()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 10, 18)];
        List<RotaHoliday> holidays = [Holiday(RotaHolidayKind.Closed, Saturday, name: "Refit")];

        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, holidays, At(Saturday, 12)));

        OpeningDay day = OpeningHoursRules.Resolve(hours, holidays, Saturday);
        Assert.IsTrue(day.IsClosedForHoliday);
        Assert.AreEqual(0, day.Windows.Count);
        Assert.AreEqual("Refit", day.Holiday.Closed!.Name);
    }

    [TestMethod]
    public void IsOpen_RemovedClosedDay_Ignored()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 10, 18)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, [Holiday(RotaHolidayKind.Closed, Saturday, active: false)], At(Saturday, 12)));
    }

    [TestMethod]
    public void IsOpen_SchoolHoliday_KeepsItsOwnHours()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Monday, 15, 19), Day(DayOfWeek.Sunday, 10, 17)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, [Holiday(RotaHolidayKind.SchoolHoliday, Monday)], At(Monday, 16)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, [Holiday(RotaHolidayKind.SchoolHoliday, Monday)], At(Monday, 11)));
    }

    [TestMethod]
    public void IsOpen_BankHoliday_UsesTheHoursOfTheDayItsStaffedLike()
    {
        // Mondays open 15:00-19:00, Sundays 10:00-17:00; the bank holiday Monday opens like a Sunday.
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Monday, 15, 19), Day(DayOfWeek.Sunday, 10, 17)];
        List<RotaHoliday> holidays = [Holiday(RotaHolidayKind.BankHoliday, Monday)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, holidays, At(Monday, 11)), "Sunday's hours");
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, holidays, At(Monday, 18)), "not Monday's");

    }

    [TestMethod]
    public void IsOpen_BankHolidayLikeADayWithNoHours_KeepsItsOwnWeekdaysHours()
    {
        // Shut on Sundays: a bank holiday Monday staffed like a Sunday (the default) still opens
        // at Monday's hours rather than shutting the venue.
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Monday, 15, 19), Day(DayOfWeek.Saturday, 10, 17)];
        List<RotaHoliday> holidays = [Holiday(RotaHolidayKind.BankHoliday, Monday)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, holidays, At(Monday, 16)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, holidays, At(Monday, 11)));
        Assert.AreEqual(1, OpeningHoursRules.Resolve(hours, holidays, Monday).Windows.Count);
    }

    [TestMethod]
    public void IsOpen_LateWindowOntoAClosedDay_StillRunsOnUntilItCloses()
    {
        // Saturday 18:00-02:00, Sunday marked closed: the window belongs to Saturday.
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 18, 2), Day(DayOfWeek.Sunday, 10, 17)];
        List<RotaHoliday> holidays = [Holiday(RotaHolidayKind.Closed, Sunday)];

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, holidays, At(Sunday, 1, 30)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, holidays, At(Sunday, 12)));
    }

    [TestMethod]
    public void IsOpen_LateWindowOffAClosedDay_IsShut()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 18, 2)];
        List<RotaHoliday> holidays = [Holiday(RotaHolidayKind.Closed, Saturday)];

        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, holidays, At(Saturday, 20)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, holidays, At(Sunday, 1, 30)), "Saturday never opened");
    }

    [TestMethod]
    public void IsOpen_BankHolidayLikeALateDay_CarriesIntoTheNextMorning()
    {
        // Monday bank holiday staffed like a Saturday (18:00-02:00): open into Tuesday's early hours.
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 18, 2)];
        List<RotaHoliday> holidays = [Holiday(RotaHolidayKind.BankHoliday, Monday, DayOfWeek.Saturday)];
        DateOnly tuesday = Monday.AddDays(1);

        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, holidays, At(Monday, 19)));
        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, holidays, At(tuesday, 1)));
        Assert.IsFalse(OpeningHoursRules.IsOpen(hours, holidays, At(tuesday, 2)));
        Assert.IsTrue(OpeningHoursRules.IsOpen(hours, holidays, At(Sunday, 1)), "the real Saturday still runs on into Sunday");
    }

    [TestMethod]
    public void Resolve_PlacesWindowsOnTheDate_ClosingNextMorningWhenLate()
    {
        List<LocationOpeningHours> hours = [Day(DayOfWeek.Saturday, 18, 2), Day(DayOfWeek.Saturday, 10, 14), Day(DayOfWeek.Saturday, 9, 9)];

        OpeningDay day = OpeningHoursRules.Resolve(hours, [], Saturday);

        Assert.AreEqual(2, day.Windows.Count, "the equal-times row is ignored");
        Assert.AreEqual(At(Saturday, 10), day.Windows[0].OpensLocal, "ordered by opening time");
        Assert.AreEqual(At(Sunday, 2), day.Windows[1].ClosesLocal);
        Assert.IsTrue(day.Holiday.IsOrdinary);
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
