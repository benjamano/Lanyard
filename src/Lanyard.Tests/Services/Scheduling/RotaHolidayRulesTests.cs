using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class RotaHolidayRulesTests
{
    // Monday 5 October 2026 to Sunday 11 October.
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static readonly Guid Host = Guid.NewGuid();

    private static ShiftTemplate Template(string name, DayOfWeek day, TemplateRuns runs = TemplateRuns.EveryWeek) => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = 1,
        LocationId = 1,
        Name = name,
        DayOfWeek = day,
        StartTime = new TimeOnly(9, 0),
        EndTime = new TimeOnly(14, 0),
        Runs = runs,
        Slots = [new ShiftTemplateSlot { ShiftTemplateId = Guid.Empty, StaffPositionId = Host, StaffPosition = new StaffPosition { Id = Host, CompanyId = 1, Name = "Host" }, Headcount = 2 }]
    };

    private static RotaHoliday Holiday(RotaHolidayKind kind, DateOnly start, DateOnly? end = null, DayOfWeek? like = null, string name = "Holiday") => new()
    {
        Id = Guid.NewGuid(),
        CompanyId = 1,
        LocationId = 1,
        Name = name,
        Kind = kind,
        StartDate = start,
        EndDate = end ?? start,
        StaffLike = like
    };

    [TestMethod]
    public void For_ClosedWinsOverBankAndSchoolHoliday()
    {
        List<RotaHoliday> holidays =
        [
            Holiday(RotaHolidayKind.SchoolHoliday, Monday, Monday.AddDays(6)),
            Holiday(RotaHolidayKind.BankHoliday, Monday),
            Holiday(RotaHolidayKind.Closed, Monday, name: "Shut")
        ];

        RotaHolidayDay day = RotaHolidayRules.For(holidays, Monday);

        Assert.IsTrue(day.IsClosed);
        Assert.AreEqual("Shut", day.Main!.Name);
        Assert.IsFalse(day.IsOrdinary);
    }

    [TestMethod]
    public void For_BankHolidayCountsAsSchoolHoliday_AndDefaultsToSunday()
    {
        RotaHolidayDay day = RotaHolidayRules.For([Holiday(RotaHolidayKind.BankHoliday, Monday)], Monday);

        Assert.IsTrue(day.IsSchoolHoliday);
        Assert.AreEqual(DayOfWeek.Sunday, day.StaffedLike);
    }

    [TestMethod]
    public void For_RemovedHolidayIgnored()
    {
        RotaHoliday removed = Holiday(RotaHolidayKind.Closed, Monday);
        removed.IsActive = false;

        Assert.IsTrue(RotaHolidayRules.For([removed], Monday).IsOrdinary);
    }

    [TestMethod]
    public void ExpandTemplates_NoHolidays_TermTimeAndEveryWeekRun_HolidayOnlyDoesNot()
    {
        List<ShiftTemplate> templates =
        [
            Template("Every", DayOfWeek.Monday),
            Template("Term", DayOfWeek.Monday, TemplateRuns.TermTimeOnly),
            Template("Hols", DayOfWeek.Monday, TemplateRuns.SchoolHolidaysOnly)
        ];

        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates(templates, Monday, Monday.AddDays(6));

        CollectionAssert.AreEquivalent(new[] { "Every", "Term" }, slots.Select(x => x.Name).ToArray());
    }

    [TestMethod]
    public void ExpandTemplates_SchoolHoliday_HolidayOnlyRuns_TermTimeDoesNot()
    {
        List<ShiftTemplate> templates =
        [
            Template("Every", DayOfWeek.Monday),
            Template("Term", DayOfWeek.Monday, TemplateRuns.TermTimeOnly),
            Template("Hols", DayOfWeek.Monday, TemplateRuns.SchoolHolidaysOnly)
        ];

        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates(templates, Monday, Monday.AddDays(6),
            [Holiday(RotaHolidayKind.SchoolHoliday, Monday.AddDays(-3), Monday.AddDays(4))]);

        CollectionAssert.AreEquivalent(new[] { "Every", "Hols" }, slots.Select(x => x.Name).ToArray());
    }

    [TestMethod]
    public void ExpandTemplates_ClosedDay_NothingThatDay()
    {
        List<ShiftTemplate> templates = [Template("Mon", DayOfWeek.Monday), Template("Tue", DayOfWeek.Tuesday)];

        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates(templates, Monday, Monday.AddDays(6),
            [Holiday(RotaHolidayKind.Closed, Monday)]);

        Assert.AreEqual(1, slots.Count);
        Assert.AreEqual("Tue", slots[0].Name);
    }

    [TestMethod]
    public void ExpandTemplates_BankHoliday_UsesStaffedLikeDaysTemplates_OnTheRealDate()
    {
        List<ShiftTemplate> templates =
        [
            Template("Monday AM", DayOfWeek.Monday),
            Template("Saturday AM", DayOfWeek.Saturday),
            Template("Sunday AM", DayOfWeek.Sunday)
        ];

        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates(templates, Monday, Monday,
            [Holiday(RotaHolidayKind.BankHoliday, Monday, like: DayOfWeek.Saturday)]);

        Assert.AreEqual(1, slots.Count);
        Assert.AreEqual("Saturday AM", slots[0].Name);
        Assert.AreEqual(Monday, slots[0].Day);
        Assert.AreEqual(RotaTime.ToUtc(Monday, new TimeOnly(9, 0)), slots[0].StartUtc);
    }

    [TestMethod]
    public void ExpandTemplates_BankHoliday_RunsHolidayOnlyTemplatesOfThatDay()
    {
        List<ShiftTemplate> templates =
        [
            Template("Sunday term", DayOfWeek.Sunday, TemplateRuns.TermTimeOnly),
            Template("Sunday hols", DayOfWeek.Sunday, TemplateRuns.SchoolHolidaysOnly)
        ];

        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates(templates, Monday, Monday,
            [Holiday(RotaHolidayKind.BankHoliday, Monday)]);

        Assert.AreEqual("Sunday hols", slots.Single().Name);
    }

    [TestMethod]
    public void ExpandTemplates_EffectiveDatesJudgedOnTheRealDay()
    {
        ShiftTemplate sunday = Template("Sunday AM", DayOfWeek.Sunday);
        sunday.EffectiveTo = Monday.AddDays(-1);

        List<DemandSlot> slots = RotaAutoBuildRules.ExpandTemplates([sunday], Monday, Monday,
            [Holiday(RotaHolidayKind.BankHoliday, Monday)]);

        Assert.AreEqual(0, slots.Count);
    }

    [TestMethod]
    public void Validate_RejectsBadInput()
    {
        Assert.IsNotNull(RotaHolidayRules.Validate(Holiday(RotaHolidayKind.SchoolHoliday, Monday, name: "  ")));
        Assert.IsNotNull(RotaHolidayRules.Validate(Holiday(RotaHolidayKind.SchoolHoliday, Monday, Monday.AddDays(-1))));
        Assert.IsNotNull(RotaHolidayRules.Validate(Holiday(RotaHolidayKind.SchoolHoliday, Monday, Monday.AddDays(400))));
        Assert.IsNotNull(RotaHolidayRules.Validate(Holiday((RotaHolidayKind)9, Monday)));
        Assert.IsNull(RotaHolidayRules.Validate(Holiday(RotaHolidayKind.BankHoliday, Monday, like: DayOfWeek.Saturday)));
    }

    [TestMethod]
    public void UkBankHolidays_2026()
    {
        DateOnly[] expected =
        [
            new(2026, 1, 1), new(2026, 4, 3), new(2026, 4, 6), new(2026, 5, 4),
            new(2026, 5, 25), new(2026, 8, 31), new(2026, 12, 25), new(2026, 12, 28)
        ];

        CollectionAssert.AreEqual(expected, UkBankHolidays.EnglandAndWales(2026).Select(x => x.Date).ToArray());
    }

    [TestMethod]
    public void UkBankHolidays_2027_ChristmasOnSaturday_BothSubstituted()
    {
        DateOnly[] expected =
        [
            new(2027, 1, 1), new(2027, 3, 26), new(2027, 3, 29), new(2027, 5, 3),
            new(2027, 5, 31), new(2027, 8, 30), new(2027, 12, 27), new(2027, 12, 28)
        ];

        CollectionAssert.AreEqual(expected, UkBankHolidays.EnglandAndWales(2027).Select(x => x.Date).ToArray());
    }

    [TestMethod]
    public void UkBankHolidays_2028_NewYearOnSaturday_And_ChristmasMonday()
    {
        List<(DateOnly Date, string Name)> days = UkBankHolidays.EnglandAndWales(2028);

        Assert.AreEqual(new DateOnly(2028, 1, 3), days[0].Date);
        Assert.AreEqual(new DateOnly(2028, 4, 14), days[1].Date, "Good Friday; Easter 2028 is 16 April");
        Assert.AreEqual(new DateOnly(2028, 12, 25), days[^2].Date);
        Assert.AreEqual(new DateOnly(2028, 12, 26), days[^1].Date);
    }
}
