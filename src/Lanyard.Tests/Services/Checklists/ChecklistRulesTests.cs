using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Checklists;

[TestClass]
public class ChecklistRulesTests
{
    // Saturdays: 28 Mar 2026 is GMT (UTC+0), 4 Apr and 17 Oct 2026 are BST (UTC+1).
    private static readonly DateOnly SaturdayGmt = new(2026, 3, 28);
    private static readonly DateOnly SaturdayBst = new(2026, 4, 4);
    private static readonly DateOnly OctoberSaturday = new(2026, 10, 17);

    private static Checklist Checklist(ChecklistDueRule rule = ChecklistDueRule.None, int offset = 0, TimeOnly? time = null,
        ChecklistDays days = ChecklistDays.EveryDay, bool onlyWhenOpen = true) => new()
    {
        CompanyId = 1,
        LocationId = 1,
        Name = "Opening",
        Days = days,
        OnlyWhenOpen = onlyWhenOpen,
        DueRule = rule,
        DueOffsetMinutes = offset,
        DueTime = time
    };

    private static LocationOpeningHours Hours(DayOfWeek day, int opens, int closes, bool active = true) => new()
    {
        LocationId = 1,
        DayOfWeek = day,
        OpensAt = new TimeOnly(opens, 0),
        ClosesAt = new TimeOnly(closes, 0),
        IsActive = active
    };

    private static RotaHolidayDay Closed(DateOnly day) => new(day,
        new RotaHoliday { CompanyId = 1, LocationId = 1, Name = "Refit", Kind = RotaHolidayKind.Closed, StartDate = day, EndDate = day },
        null, null);

    // Monday 6 Apr 2026 (BST), a bank holiday staffed like a Saturday.
    private static readonly DateOnly BankMonday = new(2026, 4, 6);

    private static RotaHolidayDay BankHoliday(DateOnly day, DayOfWeek like) => new(day, null,
        new RotaHoliday { CompanyId = 1, LocationId = 1, Name = "Easter Monday", Kind = RotaHolidayKind.BankHoliday, StartDate = day, EndDate = day, StaffLike = like },
        null);

    [TestMethod]
    public void AppliesOn_BankHoliday_RunsOnTheHoursOfTheDayItsStaffedLike()
    {
        List<LocationOpeningHours> saturdayOnly = [Hours(DayOfWeek.Saturday, 10, 18)];

        Assert.IsFalse(ChecklistRules.AppliesOn(Checklist(), BankMonday, saturdayOnly), "an ordinary Monday has no hours");
        Assert.IsTrue(ChecklistRules.AppliesOn(Checklist(), BankMonday, saturdayOnly, BankHoliday(BankMonday, DayOfWeek.Saturday)));
    }

    [TestMethod]
    public void DueUtc_BankHoliday_UsesTheStaffedLikeDaysHours()
    {
        List<LocationOpeningHours> hours = [Hours(DayOfWeek.Monday, 15, 19), Hours(DayOfWeek.Saturday, 10, 18)];
        RotaHolidayDay bank = BankHoliday(BankMonday, DayOfWeek.Saturday);

        Assert.AreEqual(new DateTime(2026, 4, 6, 9, 15, 0, DateTimeKind.Utc),
            ChecklistRules.DueUtc(Checklist(ChecklistDueRule.AfterOpening, 15), BankMonday, hours, bank), "10:15 BST");
        Assert.AreEqual(new DateTime(2026, 4, 6, 16, 30, 0, DateTimeKind.Utc),
            ChecklistRules.DueUtc(Checklist(ChecklistDueRule.BeforeClosing, 30), BankMonday, hours, bank), "17:30 BST");
        Assert.AreEqual(new DateTime(2026, 4, 6, 14, 15, 0, DateTimeKind.Utc),
            ChecklistRules.DueUtc(Checklist(ChecklistDueRule.AfterOpening, 15), BankMonday, hours), "an ordinary Monday: 15:15 BST");
    }

    [TestMethod]
    public void AppliesOn_OnlyOnTheChosenDays()
    {
        Checklist weekends = Checklist(days: ChecklistDays.Weekends, onlyWhenOpen: false);

        Assert.IsTrue(ChecklistRules.AppliesOn(weekends, SaturdayGmt, []));
        Assert.IsTrue(ChecklistRules.AppliesOn(weekends, SaturdayGmt.AddDays(1), []));
        Assert.IsFalse(ChecklistRules.AppliesOn(weekends, SaturdayGmt.AddDays(2), []));
    }

    [TestMethod]
    public void AppliesOn_SkipsDaysWithNoOpeningHours_WhenOnlyWhenOpen()
    {
        List<LocationOpeningHours> saturdayOnly = [Hours(DayOfWeek.Saturday, 10, 18)];

        Assert.IsTrue(ChecklistRules.AppliesOn(Checklist(), SaturdayGmt, saturdayOnly));
        Assert.IsFalse(ChecklistRules.AppliesOn(Checklist(), SaturdayGmt.AddDays(1), saturdayOnly));
        Assert.IsTrue(ChecklistRules.AppliesOn(Checklist(onlyWhenOpen: false), SaturdayGmt.AddDays(1), saturdayOnly));
    }

    [TestMethod]
    public void AppliesOn_LocationWithNoOpeningHoursAtAll_CountsAsOpenEveryDay()
    {
        Assert.IsTrue(ChecklistRules.AppliesOn(Checklist(), SaturdayGmt.AddDays(1), []));

        // Removed (inactive) windows don't count as hours either.
        Assert.IsTrue(ChecklistRules.AppliesOn(Checklist(), SaturdayGmt.AddDays(1), [Hours(DayOfWeek.Saturday, 10, 18, active: false)]));
    }

    [TestMethod]
    public void AppliesOn_NeverOnAClosedDay_OrWhenArchived()
    {
        Assert.IsFalse(ChecklistRules.AppliesOn(Checklist(onlyWhenOpen: false), SaturdayGmt, [], Closed(SaturdayGmt)));

        Checklist archived = Checklist(onlyWhenOpen: false);
        archived.IsActive = false;
        Assert.IsFalse(ChecklistRules.AppliesOn(archived, SaturdayGmt, []));
    }

    [TestMethod]
    public void DueUtc_AfterOpening_UsesTheFirstWindowOfTheDay_InVenueTime()
    {
        List<LocationOpeningHours> hours = [Hours(DayOfWeek.Saturday, 14, 18), Hours(DayOfWeek.Saturday, 10, 13)];

        // 10:00 + 15 min, venue time. GMT in March, BST in April.
        Assert.AreEqual(new DateTime(2026, 3, 28, 10, 15, 0, DateTimeKind.Utc), ChecklistRules.DueUtc(Checklist(ChecklistDueRule.AfterOpening, 15), SaturdayGmt, hours));
        Assert.AreEqual(new DateTime(2026, 4, 4, 9, 15, 0, DateTimeKind.Utc), ChecklistRules.DueUtc(Checklist(ChecklistDueRule.AfterOpening, 15), SaturdayBst, hours));
    }

    [TestMethod]
    public void DueUtc_BeforeClosing_PastMidnight_IsTheNextMorning()
    {
        List<LocationOpeningHours> hours = [Hours(DayOfWeek.Saturday, 10, 13), Hours(DayOfWeek.Saturday, 18, 2)];

        // Closes 02:00 BST on Sunday 18 Oct = 01:00 UTC; 30 minutes before is 00:30 UTC.
        Assert.AreEqual(new DateTime(2026, 10, 18, 0, 30, 0, DateTimeKind.Utc),
            ChecklistRules.DueUtc(Checklist(ChecklistDueRule.BeforeClosing, 30), OctoberSaturday, hours));
    }

    [TestMethod]
    public void DueUtc_BeforeClosing_ZeroOffset_IsClosingTime()
    {
        Assert.AreEqual(new DateTime(2026, 3, 28, 18, 0, 0, DateTimeKind.Utc),
            ChecklistRules.DueUtc(Checklist(ChecklistDueRule.BeforeClosing, 0), SaturdayGmt, [Hours(DayOfWeek.Saturday, 10, 18)]));
    }

    [TestMethod]
    public void DueUtc_OpeningRule_OnADayWithNoHours_IsNull()
    {
        Assert.IsNull(ChecklistRules.DueUtc(Checklist(ChecklistDueRule.AfterOpening, 15), SaturdayGmt, [Hours(DayOfWeek.Sunday, 10, 18)]));
        Assert.IsNull(ChecklistRules.DueUtc(Checklist(ChecklistDueRule.BeforeClosing, 15), SaturdayGmt, []));
    }

    [TestMethod]
    public void DueUtc_FixedTime_IsVenueLocal_AndNoneHasNoDueTime()
    {
        Assert.AreEqual(new DateTime(2026, 4, 4, 9, 0, 0, DateTimeKind.Utc),
            ChecklistRules.DueUtc(Checklist(ChecklistDueRule.FixedTime, time: new TimeOnly(10, 0)), SaturdayBst, []));
        Assert.IsNull(ChecklistRules.DueUtc(Checklist(), SaturdayBst, [Hours(DayOfWeek.Saturday, 10, 18)]));
    }

    [TestMethod]
    public void Status_FromTicksAndDueTime()
    {
        DateTime due = new(2026, 3, 28, 10, 15, 0, DateTimeKind.Utc);

        Assert.AreEqual(ChecklistRunStatus.NotStarted, ChecklistRules.Status(0, 3, due, SaturdayGmt, due.AddMinutes(-1)));
        Assert.AreEqual(ChecklistRunStatus.InProgress, ChecklistRules.Status(1, 3, due, SaturdayGmt, due.AddMinutes(-1)));
        Assert.AreEqual(ChecklistRunStatus.Missed, ChecklistRules.Status(2, 3, due, SaturdayGmt, due.AddMinutes(1)));
        Assert.AreEqual(ChecklistRunStatus.Complete, ChecklistRules.Status(3, 3, due, SaturdayGmt, due.AddHours(5)));
        Assert.AreEqual(ChecklistRunStatus.Complete, ChecklistRules.Status(0, 0, due, SaturdayGmt, due.AddHours(5)));
    }

    [TestMethod]
    public void Status_WithoutADueTime_IsMissedOnlyOnceTheVenueDayIsOver()
    {
        // 23:30 BST on 4 Apr is still Saturday at the venue (22:30 UTC); 00:30 BST Sunday is the day after.
        Assert.AreEqual(ChecklistRunStatus.InProgress, ChecklistRules.Status(1, 2, null, SaturdayBst, new DateTime(2026, 4, 4, 22, 30, 0, DateTimeKind.Utc)));
        Assert.AreEqual(ChecklistRunStatus.Missed, ChecklistRules.Status(1, 2, null, SaturdayBst, new DateTime(2026, 4, 4, 23, 30, 0, DateTimeKind.Utc)));
    }

    [TestMethod]
    public void Validate_Checklist()
    {
        Assert.IsNull(ChecklistRules.Validate(Checklist(ChecklistDueRule.AfterOpening, 15)));

        Checklist blank = Checklist();
        blank.Name = "  ";
        Assert.IsNotNull(ChecklistRules.Validate(blank));
        Assert.IsNotNull(ChecklistRules.Validate(Checklist(days: ChecklistDays.None)));
        Assert.IsNotNull(ChecklistRules.Validate(Checklist(ChecklistDueRule.BeforeClosing, -5)));
        Assert.IsNotNull(ChecklistRules.Validate(Checklist(ChecklistDueRule.AfterOpening, ChecklistRules.MaxOffsetMinutes + 1)));
        Assert.IsNotNull(ChecklistRules.Validate(Checklist(ChecklistDueRule.FixedTime)));
        Assert.IsNotNull(ChecklistRules.Validate(Checklist((ChecklistDueRule)99)));
    }

    [TestMethod]
    public void Validate_Task()
    {
        Assert.IsNull(ChecklistRules.Validate(new ChecklistTask { ChecklistId = Guid.NewGuid(), Title = "Fridge temperature", Response = ChecklistResponse.Number, Unit = "°C" }));
        Assert.IsNotNull(ChecklistRules.Validate(new ChecklistTask { ChecklistId = Guid.NewGuid(), Title = " " }));
        Assert.IsNotNull(ChecklistRules.Validate(new ChecklistTask { ChecklistId = Guid.NewGuid(), Title = new string('x', ChecklistRules.MaxTaskTitleLength + 1) }));
        Assert.IsNotNull(ChecklistRules.Validate(new ChecklistTask { ChecklistId = Guid.NewGuid(), Title = "Temp", Response = ChecklistResponse.Number, Unit = "degrees celsius" }));
        Assert.IsNotNull(ChecklistRules.Validate(new ChecklistTask { ChecklistId = Guid.NewGuid(), Title = "Temp", Response = (ChecklistResponse)9 }));
    }

    [TestMethod]
    public void Labels_ReadNaturally()
    {
        Assert.AreEqual("Every day", ChecklistRules.DaysLabel(ChecklistDays.EveryDay));
        Assert.AreEqual("Weekdays", ChecklistRules.DaysLabel(ChecklistDays.Weekdays));
        Assert.AreEqual("Mon, Wed, Fri", ChecklistRules.DaysLabel(ChecklistDays.Monday | ChecklistDays.Wednesday | ChecklistDays.Friday));
        Assert.AreEqual("Weekends", ChecklistRules.DaysLabel(ChecklistDays.Saturday | ChecklistDays.Sunday));
        Assert.AreEqual("Tue, Sat", ChecklistRules.DaysLabel(ChecklistDays.Saturday | ChecklistDays.Tuesday));

        Assert.AreEqual("15 min after opening", ChecklistRules.DueLabel(Checklist(ChecklistDueRule.AfterOpening, 15)));
        Assert.AreEqual("By closing", ChecklistRules.DueLabel(Checklist(ChecklistDueRule.BeforeClosing, 0)));
        Assert.AreEqual("1 hr 30 min before closing", ChecklistRules.DueLabel(Checklist(ChecklistDueRule.BeforeClosing, 90)));
        Assert.AreEqual("By 10:00", ChecklistRules.DueLabel(Checklist(ChecklistDueRule.FixedTime, time: new TimeOnly(10, 0))));
        Assert.AreEqual("No set time", ChecklistRules.DueLabel(Checklist()));
    }
}
