using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class YoungWorkerRulesTests
{
    // Monday 23 Nov 2026 (GMT, so local time = UTC that week).
    private static readonly DateOnly Monday = new(2026, 11, 23);

    private static readonly YoungWorkerProfile Under18 = new(new DateOnly(2010, 1, 1), YoungWorkerNightWindow.TenToSix);

    private static WorkBlock Block(DateOnly day, int startHour, int startMinute, int endHour, int endMinute, int breakMinutes = 0)
    {
        TimeOnly start = new(startHour, startMinute);
        TimeOnly end = new(endHour, endMinute);
        DateOnly endDay = end <= start ? day.AddDays(1) : day;

        return new WorkBlock(Guid.NewGuid(), RotaTime.ToUtc(day, start), RotaTime.ToUtc(endDay, end), breakMinutes);
    }

    private static List<YoungWorkerRule> Rules(WorkBlock candidate, params WorkBlock[] others) =>
        Rules(Under18, candidate, others);

    private static List<YoungWorkerRule> Rules(YoungWorkerProfile profile, WorkBlock candidate, params WorkBlock[] others) =>
        YoungWorkerRules.Evaluate(profile, candidate, others).Select(x => x.Rule).ToList();

    // --- Who it applies to ---

    [TestMethod]
    public void Evaluate_OnTheir18thBirthday_NothingApplies()
    {
        YoungWorkerProfile turns18Monday = new(Monday.AddYears(-18), YoungWorkerNightWindow.TenToSix);

        Assert.AreEqual(0, Rules(turns18Monday, Block(Monday, 18, 0, 23, 30, 30)).Count);
    }

    [TestMethod]
    public void Evaluate_DayBeforeTheir18thBirthday_LimitsApply()
    {
        YoungWorkerProfile turns18Tuesday = new(Monday.AddDays(1).AddYears(-18), YoungWorkerNightWindow.TenToSix);

        CollectionAssert.Contains(Rules(turns18Tuesday, Block(Monday, 18, 0, 23, 0, 30)), YoungWorkerRule.NightWork);
    }

    [TestMethod]
    public void Evaluate_UnderSchoolLeavingAge_AlwaysBreaches_EvenForAShortDayShift()
    {
        YoungWorkerProfile atSchool = new(new DateOnly(2011, 1, 15), YoungWorkerNightWindow.TenToSix);

        List<YoungWorkerBreach> breaches = YoungWorkerRules.Evaluate(atSchool, Block(Monday, 10, 0, 12, 0), []);

        Assert.AreEqual(YoungWorkerRule.UnderSchoolLeavingAge, breaches.Single().Rule);
        StringAssert.Contains(breaches.Single().Message, "child employment");
    }

    // --- Hours ---

    [TestMethod]
    public void Evaluate_EightHoursADay_IsTheLimit()
    {
        Assert.AreEqual(0, Rules(Block(Monday, 9, 0, 17, 30, 30)).Count);
        CollectionAssert.Contains(Rules(Block(Monday, 9, 0, 17, 45, 30)), YoungWorkerRule.DailyHours);
    }

    [TestMethod]
    public void Evaluate_DailyHours_CountEveryShiftThatDay()
    {
        WorkBlock morning = Block(Monday, 9, 0, 13, 0);

        CollectionAssert.Contains(Rules(Block(Monday, 16, 0, 20, 30), morning), YoungWorkerRule.DailyHours);
        CollectionAssert.DoesNotContain(Rules(Block(Monday, 16, 0, 20, 0), morning), YoungWorkerRule.DailyHours);
    }

    [TestMethod]
    public void Evaluate_FortyHoursAWeek_IsTheLimit()
    {
        WorkBlock[] monToThu = Enumerable.Range(0, 4).Select(d => Block(Monday.AddDays(d), 9, 0, 17, 30, 30)).ToArray();

        CollectionAssert.DoesNotContain(Rules(Block(Monday.AddDays(4), 9, 0, 17, 30, 30), monToThu), YoungWorkerRule.WeeklyHours);

        WorkBlock[] monToFri = [.. monToThu, Block(Monday.AddDays(4), 9, 0, 17, 30, 30)];
        List<YoungWorkerBreach> saturday = YoungWorkerRules.Evaluate(Under18, Block(Monday.AddDays(5), 10, 0, 10, 30), monToFri);

        YoungWorkerBreach weekly = saturday.Single(x => x.Rule == YoungWorkerRule.WeeklyHours);
        StringAssert.Contains(weekly.Message, "40.5 h");
    }

    // --- Breaks ---

    [TestMethod]
    public void Evaluate_Break_NeededOnlyOverFourAndAHalfHours()
    {
        Assert.AreEqual(0, Rules(Block(Monday, 10, 0, 14, 30)).Count);
        CollectionAssert.Contains(Rules(Block(Monday, 10, 0, 15, 0, 15)), YoungWorkerRule.Break);
        CollectionAssert.DoesNotContain(Rules(Block(Monday, 10, 0, 15, 30, 30)), YoungWorkerRule.Break);
    }

    [TestMethod]
    public void RequiredBreakMinutes_RaisesALongShiftTo30_LeavesOthersAlone()
    {
        Assert.AreEqual(30, YoungWorkerRules.RequiredBreakMinutes(Under18, Block(Monday, 9, 0, 15, 0)));
        Assert.AreEqual(0, YoungWorkerRules.RequiredBreakMinutes(Under18, Block(Monday, 9, 0, 13, 0)));
        Assert.AreEqual(45, YoungWorkerRules.RequiredBreakMinutes(Under18, Block(Monday, 9, 0, 17, 0, 45)));

        YoungWorkerProfile adult = new(Monday.AddYears(-18), YoungWorkerNightWindow.TenToSix);
        Assert.AreEqual(0, YoungWorkerRules.RequiredBreakMinutes(adult, Block(Monday, 9, 0, 17, 0)));
    }

    // --- Rest ---

    [TestMethod]
    public void Evaluate_TwelveHoursBetweenDays_IsEnough()
    {
        WorkBlock mondayEvening = Block(Monday, 15, 0, 21, 0, 30);

        CollectionAssert.DoesNotContain(Rules(Block(Monday.AddDays(1), 9, 0, 13, 0), mondayEvening), YoungWorkerRule.DailyRest);

        List<YoungWorkerBreach> early = YoungWorkerRules.Evaluate(Under18, Block(Monday.AddDays(1), 8, 30, 12, 30), [mondayEvening]);
        StringAssert.Contains(early.Single(x => x.Rule == YoungWorkerRule.DailyRest).Message, "11.5 h");
    }

    [TestMethod]
    public void Evaluate_SplitShiftOnTheSameDay_IsNotShortRest()
    {
        Assert.AreEqual(0, Rules(Block(Monday, 17, 0, 21, 0), Block(Monday, 9, 0, 13, 0)).Count);
    }

    [TestMethod]
    public void EvaluateWeek_NeedsFortyEightHoursOffInARow()
    {
        // Monday to Friday mornings: the whole weekend is free.
        WorkBlock[] weekdays = Enumerable.Range(0, 5).Select(d => Block(Monday.AddDays(d), 9, 0, 13, 0)).ToArray();
        Assert.AreEqual(0, YoungWorkerRules.EvaluateWeek(Under18, Monday, weekdays).Count);

        // Every day of the week: never 48 hours off.
        WorkBlock[] everyDay = Enumerable.Range(0, 7).Select(d => Block(Monday.AddDays(d), 9, 0, 13, 0)).ToArray();
        CollectionAssert.Contains(YoungWorkerRules.EvaluateWeek(Under18, Monday, everyDay).Select(x => x.Message).ToList(),
            "Under 18: no 48 hours off in a row in the week of Mon 23 Nov.");
    }

    [TestMethod]
    public void EvaluateWeek_TimeOffInTheWeekBefore_DoesNotCountForThisWeek()
    {
        // Sat 21 Nov afternoon to Tue 24 Nov morning is free, but only 33 h of it is in this week.
        WorkBlock[] lastWeek = Enumerable.Range(-7, 6).Select(d => Block(Monday.AddDays(d), 9, 0, 13, 0)).ToArray();
        WorkBlock[] thisWeek = Enumerable.Range(1, 6).Select(d => Block(Monday.AddDays(d), 9, 0, 13, 0)).ToArray();

        List<YoungWorkerBreach> breaches = YoungWorkerRules.EvaluateWeek(Under18, Monday, [.. lastWeek, .. thisWeek]);

        Assert.IsTrue(breaches.Any(x => x.Message.Contains("48 hours")));
    }

    [TestMethod]
    public void Evaluate_WeekStillBeingBuilt_HasItsRestAhead()
    {
        Assert.AreEqual(0, Rules(Block(Monday.AddDays(1), 9, 0, 13, 0), Block(Monday, 9, 0, 13, 0)).Count);
    }

    // --- Night ---

    [TestMethod]
    public void Evaluate_NightWindow_EndingAtTenIsFine_LaterIsNot()
    {
        CollectionAssert.DoesNotContain(Rules(Block(Monday, 14, 0, 22, 0, 30)), YoungWorkerRule.NightWork);
        CollectionAssert.Contains(Rules(Block(Monday, 14, 15, 22, 15, 30)), YoungWorkerRule.NightWork);
        CollectionAssert.DoesNotContain(Rules(Block(Monday, 6, 0, 10, 0)), YoungWorkerRule.NightWork);
        CollectionAssert.Contains(Rules(Block(Monday, 5, 30, 9, 30)), YoungWorkerRule.NightWork);
    }

    [TestMethod]
    public void Evaluate_ElevenToSevenContract_AllowsHalfTen_ButNotHalfSixInTheMorning()
    {
        YoungWorkerProfile lateContract = Under18 with { NightWindow = YoungWorkerNightWindow.ElevenToSeven };

        Assert.AreEqual(0, Rules(lateContract, Block(Monday, 15, 0, 22, 30, 30)).Count);
        CollectionAssert.Contains(Rules(lateContract, Block(Monday, 6, 30, 10, 30)), YoungWorkerRule.NightWork);

        StringAssert.Contains(YoungWorkerRules.Evaluate(lateContract, Block(Monday, 18, 0, 23, 30, 30), []).Single().Message, "23:00 and 07:00");
    }

    [TestMethod]
    public void Evaluate_MidnightToFour_IsNeverAllowed()
    {
        YoungWorkerProfile lateContract = Under18 with { NightWindow = YoungWorkerNightWindow.ElevenToSeven };

        Assert.AreEqual(YoungWorkerRule.CoreNight, Rules(lateContract, Block(Monday, 3, 0, 6, 0)).Single());
        Assert.AreEqual(YoungWorkerRule.CoreNight, Rules(Block(Monday, 20, 0, 0, 30, 30)).Single(x => x is YoungWorkerRule.CoreNight or YoungWorkerRule.NightWork));
    }

    [TestMethod]
    public void Evaluate_NightWindow_FollowsTheClocksGoingForward()
    {
        // Sun 28 Mar 2027: 06:00 BST is 05:00 UTC, still after the local night window.
        DateOnly springForward = new(2027, 3, 28);

        CollectionAssert.DoesNotContain(Rules(Block(springForward, 6, 0, 10, 0)), YoungWorkerRule.NightWork);
        CollectionAssert.Contains(Rules(Block(springForward, 5, 30, 9, 30)), YoungWorkerRule.NightWork);
    }

    [TestMethod]
    public void Evaluate_NightWindow_FollowsTheClocksGoingBack()
    {
        // Sat 30 Oct 2027 is still BST: 22:00 local is 21:00 UTC.
        DateOnly beforeFallBack = new(2027, 10, 30);

        CollectionAssert.DoesNotContain(Rules(Block(beforeFallBack, 14, 0, 22, 0, 30)), YoungWorkerRule.NightWork);
        CollectionAssert.Contains(Rules(Block(beforeFallBack, 14, 15, 22, 15, 30)), YoungWorkerRule.NightWork);
    }

    // --- What counts against this shift ---

    [TestMethod]
    public void Evaluate_EditingAShift_DoesNotCountItTwice()
    {
        WorkBlock existing = Block(Monday, 9, 0, 17, 30, 30);

        Assert.AreEqual(0, YoungWorkerRules.Evaluate(Under18, existing with { BreakMinutes = 30 }, [existing]).Count);
    }

    [TestMethod]
    public void Evaluate_ProblemsAlreadyOnTheRota_AreNotBlamedOnThisShift()
    {
        WorkBlock longTuesday = Block(Monday.AddDays(1), 8, 0, 18, 0, 30);

        Assert.AreEqual(0, Rules(Block(Monday.AddDays(3), 10, 0, 14, 0), longTuesday).Count);
    }

    [TestMethod]
    public void Evaluate_DayAlreadyOverTheLimit_AddingToItIsStillABreach()
    {
        // A manager has already given them 9.5 h on Monday by hand.
        WorkBlock longMonday = Block(Monday, 8, 0, 18, 0, 30);

        CollectionAssert.Contains(Rules(Block(Monday, 19, 0, 20, 0), longMonday), YoungWorkerRule.DailyHours);
    }

    [TestMethod]
    public void Evaluate_WeekAlreadyOver40_AnotherShiftThatWeekIsStillABreach()
    {
        WorkBlock[] fiftyHours = Enumerable.Range(0, 5).Select(d => Block(Monday.AddDays(d), 8, 0, 18, 30, 30)).ToArray();

        CollectionAssert.Contains(Rules(Block(Monday.AddDays(5), 10, 0, 11, 0), fiftyHours), YoungWorkerRule.WeeklyHours);
    }

    [TestMethod]
    public void Evaluate_ShortRestAlreadyThere_SplitShiftLaterThatDayIsNotBlamed()
    {
        // 21:00 Monday to 08:00 Tuesday is already short; an afternoon shift on Tuesday doesn't change it.
        WorkBlock mondayLate = Block(Monday, 13, 0, 21, 0, 30);
        WorkBlock tuesdayEarly = Block(Monday.AddDays(1), 8, 0, 11, 0);

        CollectionAssert.DoesNotContain(Rules(Block(Monday.AddDays(1), 14, 0, 16, 0), mondayLate, tuesdayEarly), YoungWorkerRule.DailyRest);
        CollectionAssert.Contains(Rules(Block(Monday.AddDays(1), 7, 0, 8, 0), mondayLate, tuesdayEarly), YoungWorkerRule.DailyRest,
            "a shift that starts the day earlier still shortens the rest");
    }

    [TestMethod]
    public void Weekly_InTheWeekTheyTurn18_OnlyTheUnder18DaysCount()
    {
        // Turns 18 on Thursday: 24 h Monday to Wednesday, then 24 h as an adult.
        YoungWorkerProfile turns18Thursday = new(Monday.AddDays(3).AddYears(-18), YoungWorkerNightWindow.TenToSix);
        WorkBlock[] week = Enumerable.Range(0, 6).Select(d => Block(Monday.AddDays(d), 9, 0, 17, 30, 30)).ToArray();

        Assert.IsFalse(YoungWorkerRules.EvaluateWeek(turns18Thursday, Monday, week).Any(x => x.Rule == YoungWorkerRule.WeeklyHours));
        CollectionAssert.DoesNotContain(Rules(turns18Thursday, week[2], [.. week.Where(x => x.Id != week[2].Id)]), YoungWorkerRule.WeeklyHours);
    }

    [TestMethod]
    public void Evaluate_ShiftOverload_IgnoresInactiveShifts()
    {
        Shift removed = new() { Id = Guid.NewGuid(), LocationId = 1, StartUtc = RotaTime.ToUtc(Monday, new TimeOnly(8, 0)), EndUtc = RotaTime.ToUtc(Monday, new TimeOnly(13, 0)), IsActive = false, CreateByUserId = "m" };
        Shift candidate = new() { Id = Guid.NewGuid(), LocationId = 1, StartUtc = RotaTime.ToUtc(Monday, new TimeOnly(14, 0)), EndUtc = RotaTime.ToUtc(Monday, new TimeOnly(18, 0)), IsActive = true, CreateByUserId = "m" };

        Assert.AreEqual(0, YoungWorkerRules.Evaluate(Under18, candidate, [removed]).Count);
    }
}
