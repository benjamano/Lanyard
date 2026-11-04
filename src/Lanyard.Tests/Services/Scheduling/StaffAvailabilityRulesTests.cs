using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class StaffAvailabilityRulesTests
{
    // Tuesday 6 October 2026 - British Summer Time.
    private static readonly DateOnly Tuesday = new(2026, 10, 6);

    private static StaffAvailability Window(DayOfWeek day, int startHour, int endHour, AvailabilityKind kind = AvailabilityKind.Unavailable,
        DateOnly? from = null, DateOnly? to = null, bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = 1,
        UserId = "u1",
        DayOfWeek = day,
        StartTime = new TimeOnly(startHour, 0),
        EndTime = new TimeOnly(endHour, 0),
        Kind = kind,
        EffectiveFrom = from,
        EffectiveTo = to,
        IsActive = isActive
    };

    private static (DateTime Start, DateTime End) Shift(DateOnly day, int startHour, int endHour) =>
        (RotaTime.ToUtc(day, new TimeOnly(startHour, 0)),
         endHour > startHour ? RotaTime.ToUtc(day, new TimeOnly(endHour, 0)) : RotaTime.ToUtc(day.AddDays(1), new TimeOnly(endHour, 0)));

    private static StaffAvailability? ConflictFor(StaffAvailability row, (DateTime Start, DateTime End) shift) =>
        StaffAvailabilityRules.Conflict([row], shift.Start, shift.End);

    // --- Conflict ---

    [TestMethod]
    public void Conflict_ShiftInsideUnavailableWindow_Conflicts()
    {
        Assert.IsNotNull(ConflictFor(Window(DayOfWeek.Tuesday, 17, 23), Shift(Tuesday, 18, 22)));
    }

    [TestMethod]
    public void Conflict_PartialOverlap_Conflicts()
    {
        Assert.IsNotNull(ConflictFor(Window(DayOfWeek.Tuesday, 17, 23), Shift(Tuesday, 12, 18)));
    }

    [TestMethod]
    public void Conflict_ShiftEndingWhenWindowStarts_DoesNotConflict()
    {
        Assert.IsNull(ConflictFor(Window(DayOfWeek.Tuesday, 17, 23), Shift(Tuesday, 9, 17)));
    }

    [TestMethod]
    public void Conflict_OtherWeekday_DoesNotConflict()
    {
        Assert.IsNull(ConflictFor(Window(DayOfWeek.Wednesday, 9, 17), Shift(Tuesday, 9, 17)));
    }

    [TestMethod]
    public void Conflict_PreferredWindow_NeverConflicts()
    {
        Assert.IsNull(ConflictFor(Window(DayOfWeek.Tuesday, 9, 17, AvailabilityKind.Preferred), Shift(Tuesday, 9, 17)));
    }

    [TestMethod]
    public void Conflict_InactiveWindow_Ignored()
    {
        Assert.IsNull(ConflictFor(Window(DayOfWeek.Tuesday, 9, 17, isActive: false), Shift(Tuesday, 9, 17)));
    }

    [TestMethod]
    public void Conflict_AllDay_CoversTheWholeDay()
    {
        StaffAvailability allDay = Window(DayOfWeek.Tuesday, 0, 0);

        Assert.IsNotNull(ConflictFor(allDay, Shift(Tuesday, 6, 8)));
        Assert.IsNotNull(ConflictFor(allDay, Shift(Tuesday, 22, 2)));
        Assert.IsNull(ConflictFor(allDay, Shift(Tuesday.AddDays(1), 0, 4)));
    }

    [TestMethod]
    public void Conflict_PastMidnightWindow_ReachesIntoNextMorning()
    {
        // Monday 22:00 to Tuesday 02:00: a Tuesday 01:00 shift clashes, a Tuesday 02:00 one doesn't.
        StaffAvailability lateMonday = Window(DayOfWeek.Monday, 22, 2);

        Assert.IsNotNull(ConflictFor(lateMonday, Shift(Tuesday, 1, 4)));
        Assert.IsNull(ConflictFor(lateMonday, Shift(Tuesday, 2, 6)));
    }

    [TestMethod]
    public void Conflict_OvernightShift_CaughtByNextDaysWindow()
    {
        // A Tuesday 20:00-04:00 shift runs into Wednesday, which is unavailable until 06:00.
        Assert.IsNotNull(ConflictFor(Window(DayOfWeek.Wednesday, 0, 6), Shift(Tuesday, 20, 4)));
    }

    [TestMethod]
    public void Conflict_EffectiveDates_LimitTheWindow()
    {
        StaffAvailability fromNextWeek = Window(DayOfWeek.Tuesday, 9, 17, from: Tuesday.AddDays(7));
        StaffAvailability untilLastWeek = Window(DayOfWeek.Tuesday, 9, 17, to: Tuesday.AddDays(-7));
        StaffAvailability thisWeekOnly = Window(DayOfWeek.Tuesday, 9, 17, from: Tuesday, to: Tuesday);

        Assert.IsNull(ConflictFor(fromNextWeek, Shift(Tuesday, 10, 12)));
        Assert.IsNotNull(ConflictFor(fromNextWeek, Shift(Tuesday.AddDays(7), 10, 12)));
        Assert.IsNull(ConflictFor(untilLastWeek, Shift(Tuesday, 10, 12)));
        Assert.IsNotNull(ConflictFor(thisWeekOnly, Shift(Tuesday, 10, 12)));
    }

    [TestMethod]
    public void Conflict_ClocksGoBack_UsesWallClockTimes()
    {
        // Sunday 25 October 2026: the clocks go back at 02:00. An 09:00-17:00 local window still
        // matches a 09:00-17:00 local shift, now in GMT.
        DateOnly sunday = new(2026, 10, 25);

        Assert.IsNotNull(ConflictFor(Window(DayOfWeek.Sunday, 9, 17), Shift(sunday, 9, 17)));
        Assert.IsNull(ConflictFor(Window(DayOfWeek.Sunday, 9, 17), Shift(sunday, 17, 20)));
    }

    [TestMethod]
    public void WindowOn_AllDayOnClockChange_IsTwentyFiveHours()
    {
        (DateTime start, DateTime end) = StaffAvailabilityRules.WindowOn(Window(DayOfWeek.Sunday, 0, 0), new DateOnly(2026, 10, 25));

        Assert.AreEqual(TimeSpan.FromHours(25), end - start);
    }

    // --- Preferred ---

    [TestMethod]
    public void Preferred_OnlyWhenTheWindowCoversTheWholeShift()
    {
        StaffAvailability mornings = Window(DayOfWeek.Tuesday, 8, 14, AvailabilityKind.Preferred);

        Assert.IsNotNull(StaffAvailabilityRules.Preferred([mornings], Shift(Tuesday, 9, 13).Start, Shift(Tuesday, 9, 13).End));
        Assert.IsNull(StaffAvailabilityRules.Preferred([mornings], Shift(Tuesday, 12, 16).Start, Shift(Tuesday, 12, 16).End));
    }

    // --- Describe ---

    [TestMethod]
    public void Describe_ReadsLikeAWeek()
    {
        Assert.AreEqual("Tuesdays, 17:00–23:00", StaffAvailabilityRules.Describe(Window(DayOfWeek.Tuesday, 17, 23)));
        Assert.AreEqual("Sundays, all day", StaffAvailabilityRules.Describe(Window(DayOfWeek.Sunday, 0, 0)));
        Assert.AreEqual("Fridays, 22:00–02:00 from 5 Jan 2027", StaffAvailabilityRules.Describe(Window(DayOfWeek.Friday, 22, 2, from: new DateOnly(2027, 1, 5))));
        Assert.AreEqual("Mondays, 09:00–12:00 until 1 Dec 2026", StaffAvailabilityRules.Describe(Window(DayOfWeek.Monday, 9, 12, to: new DateOnly(2026, 12, 1))));
        Assert.AreEqual("Mondays, 09:00–12:00 from 1 Nov 2026 to 1 Dec 2026",
            StaffAvailabilityRules.Describe(Window(DayOfWeek.Monday, 9, 12, from: new DateOnly(2026, 11, 1), to: new DateOnly(2026, 12, 1))));
    }

    // --- FindOverlap / Validate ---

    [TestMethod]
    public void FindOverlap_SameDayOverlappingTimes_Found()
    {
        string? overlap = StaffAvailabilityRules.FindOverlap([Window(DayOfWeek.Tuesday, 9, 17), Window(DayOfWeek.Tuesday, 16, 20, AvailabilityKind.Preferred)]);

        Assert.AreEqual("Tuesdays, 09:00–17:00 overlaps Tuesdays, 16:00–20:00.", overlap);
    }

    [TestMethod]
    public void FindOverlap_TouchingWindows_Allowed()
    {
        Assert.IsNull(StaffAvailabilityRules.FindOverlap([Window(DayOfWeek.Tuesday, 9, 17), Window(DayOfWeek.Tuesday, 17, 20)]));
    }

    [TestMethod]
    public void FindOverlap_PastMidnightIntoNextDay_Found()
    {
        Assert.IsNotNull(StaffAvailabilityRules.FindOverlap([Window(DayOfWeek.Monday, 22, 2), Window(DayOfWeek.Tuesday, 1, 5)]));
    }

    [TestMethod]
    public void FindOverlap_SundayNightIntoMonday_Found()
    {
        Assert.IsNotNull(StaffAvailabilityRules.FindOverlap([Window(DayOfWeek.Sunday, 22, 3), Window(DayOfWeek.Monday, 0, 0)]));
    }

    [TestMethod]
    public void FindOverlap_DifferentDates_Allowed()
    {
        StaffAvailability untilNovember = Window(DayOfWeek.Tuesday, 9, 17, to: new DateOnly(2026, 10, 31));
        StaffAvailability fromNovember = Window(DayOfWeek.Tuesday, 12, 20, from: new DateOnly(2026, 11, 1));

        Assert.IsNull(StaffAvailabilityRules.FindOverlap([untilNovember, fromNovember]));
    }

    [TestMethod]
    public void FindOverlap_InactiveIgnored()
    {
        Assert.IsNull(StaffAvailabilityRules.FindOverlap([Window(DayOfWeek.Tuesday, 9, 17), Window(DayOfWeek.Tuesday, 9, 17, isActive: false)]));
    }

    [TestMethod]
    public void Validate_EndDateBeforeStart_Refused()
    {
        Assert.IsNotNull(StaffAvailabilityRules.Validate(Window(DayOfWeek.Tuesday, 9, 17, from: Tuesday, to: Tuesday.AddDays(-1))));
        Assert.IsNull(StaffAvailabilityRules.Validate(Window(DayOfWeek.Tuesday, 9, 17, from: Tuesday, to: Tuesday)));
    }
}
