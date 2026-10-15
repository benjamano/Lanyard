#nullable enable

using Lanyard.Application.Services;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Automation;

[TestClass]
public class AutomationConditionEvaluatorTests
{
    private const int RuleCompanyId = 1;

    // 15:00 UTC is 16:00 in London in July (BST) but 15:00 in January (GMT).
    private static readonly DateTime SummerAfternoonUtc = new(2026, 7, 15, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WinterAfternoonUtc = new(2026, 1, 14, 15, 0, 0, DateTimeKind.Utc);

    private static readonly Guid KioskId = Guid.NewGuid();

    private static AutomationRuleCondition Condition(AutomationConditionType type, string json, int sortOrder = 0, bool isActive = true)
    {
        return new AutomationRuleCondition
        {
            Id = Guid.NewGuid(),
            ConditionType = type,
            ParametersJson = json,
            SortOrder = sortOrder,
            IsActive = isActive
        };
    }

    private static AutomationRuleCondition TimeWindow(TimeOnly start, TimeOnly end)
    {
        return Condition(AutomationConditionType.TimeWindow, AutomationConditionParameters.Serialize(new TimeWindowConditionParameters(start, end)));
    }

    private static AutomationRuleCondition Days(params DayOfWeek[] days)
    {
        return Condition(AutomationConditionType.DaysOfWeek, AutomationConditionParameters.Serialize(new DaysOfWeekConditionParameters(AutomationScheduleDays.Serialize(days))));
    }

    private static AutomationRuleCondition KioskInGame(Guid clientId, bool inGame)
    {
        return Condition(AutomationConditionType.ClientGameStatus, AutomationConditionParameters.Serialize(new ClientGameStatusConditionParameters(clientId, inGame)));
    }

    private static AutomationConditionCheck Evaluate(
        DateTime nowUtc,
        IEnumerable<AutomationRuleCondition> conditions,
        bool kioskInGame = false,
        int kioskCompanyId = RuleCompanyId,
        bool kioskExists = true)
    {
        return AutomationConditionEvaluator.Evaluate(
            conditions,
            RuleCompanyId,
            nowUtc,
            id => kioskExists && id == KioskId ? new AutomationClientInfo(kioskCompanyId, "Arena") : null,
            id => kioskInGame && id == KioskId);
    }

    [TestMethod]
    public void Evaluate_PassesWithNoConditions()
    {
        AutomationConditionCheck check = Evaluate(SummerAfternoonUtc, []);

        Assert.IsTrue(check.Passed);
    }

    [TestMethod]
    public void Evaluate_IgnoresInactiveConditions()
    {
        AutomationRuleCondition removed = Condition(AutomationConditionType.TimeWindow, "not json", isActive: false);

        AutomationConditionCheck check = Evaluate(SummerAfternoonUtc, [removed]);

        Assert.IsTrue(check.Passed, "A soft-deleted condition must not stop the rule.");
    }

    [TestMethod]
    public void TimeWindow_UsesVenueTimeNotUtc_InSummer()
    {
        // 15:00 UTC in July is 16:00 venue time, so a 16:00-17:00 window matches and a
        // 15:00-16:00 one doesn't. Server-local time in the UTC container would get both wrong.
        Assert.IsTrue(Evaluate(SummerAfternoonUtc, [TimeWindow(new(16, 0), new(17, 0))]).Passed);

        AutomationConditionCheck utcWindow = Evaluate(SummerAfternoonUtc, [TimeWindow(new(15, 0), new(16, 0))]);
        Assert.IsFalse(utcWindow.Passed);
        Assert.IsFalse(utcWindow.IsMisconfigured, "Being outside the window is a normal skip, not a broken rule.");
        StringAssert.Contains(utcWindow.Reason, "16:00");
    }

    [TestMethod]
    public void TimeWindow_UsesVenueTime_InWinter()
    {
        Assert.IsTrue(Evaluate(WinterAfternoonUtc, [TimeWindow(new(15, 0), new(16, 0))]).Passed);
        Assert.IsFalse(Evaluate(WinterAfternoonUtc, [TimeWindow(new(16, 0), new(17, 0))]).Passed);
    }

    [TestMethod]
    public void IsWithinWindow_IncludesStartAndExcludesEnd()
    {
        Assert.IsTrue(AutomationConditionEvaluator.IsWithinWindow(new(18, 0), new(22, 0), new(18, 0)));
        Assert.IsTrue(AutomationConditionEvaluator.IsWithinWindow(new(18, 0), new(22, 0), new(21, 59)));
        Assert.IsFalse(AutomationConditionEvaluator.IsWithinWindow(new(18, 0), new(22, 0), new(22, 0)));
        Assert.IsFalse(AutomationConditionEvaluator.IsWithinWindow(new(18, 0), new(22, 0), new(17, 59)));
    }

    [TestMethod]
    public void IsWithinWindow_WrapsPastMidnight_WhenEndIsBeforeStart()
    {
        Assert.IsTrue(AutomationConditionEvaluator.IsWithinWindow(new(22, 0), new(2, 0), new(23, 30)));
        Assert.IsTrue(AutomationConditionEvaluator.IsWithinWindow(new(22, 0), new(2, 0), new(0, 0)));
        Assert.IsTrue(AutomationConditionEvaluator.IsWithinWindow(new(22, 0), new(2, 0), new(1, 59)));
        Assert.IsFalse(AutomationConditionEvaluator.IsWithinWindow(new(22, 0), new(2, 0), new(2, 0)));
        Assert.IsFalse(AutomationConditionEvaluator.IsWithinWindow(new(22, 0), new(2, 0), new(12, 0)));
    }

    [TestMethod]
    public void IsWithinWindow_FailsClosed_WhenStartEqualsEnd()
    {
        Assert.IsFalse(AutomationConditionEvaluator.IsWithinWindow(new(9, 0), new(9, 0), new(9, 0)));
    }

    [TestMethod]
    public void DaysOfWeek_UsesTheVenueLocalDay()
    {
        // 23:30 UTC on Tuesday 14 July is 00:30 on Wednesday in London.
        DateTime lateTuesdayUtc = new(2026, 7, 14, 23, 30, 0, DateTimeKind.Utc);

        Assert.IsTrue(Evaluate(lateTuesdayUtc, [Days(DayOfWeek.Wednesday)]).Passed);

        AutomationConditionCheck tuesday = Evaluate(lateTuesdayUtc, [Days(DayOfWeek.Tuesday)]);
        Assert.IsFalse(tuesday.Passed);
        StringAssert.Contains(tuesday.Reason, "Wednesday");
    }

    [TestMethod]
    public void DaysOfWeek_FailsClosed_WhenNoDaysAreSet()
    {
        AutomationRuleCondition blank = Condition(AutomationConditionType.DaysOfWeek, AutomationConditionParameters.Serialize(new DaysOfWeekConditionParameters("")));

        AutomationConditionCheck check = Evaluate(SummerAfternoonUtc, [blank]);

        Assert.IsFalse(check.Passed, "Unlike a schedule, a blank days condition must not mean every day.");
        Assert.IsTrue(check.IsMisconfigured);
    }

    [TestMethod]
    public void ClientGameStatus_PassesWhenTheKioskMatches()
    {
        Assert.IsTrue(Evaluate(SummerAfternoonUtc, [KioskInGame(KioskId, inGame: true)], kioskInGame: true).Passed);
        Assert.IsTrue(Evaluate(SummerAfternoonUtc, [KioskInGame(KioskId, inGame: false)], kioskInGame: false).Passed);
    }

    [TestMethod]
    public void ClientGameStatus_ReportsTheKioskByName_WhenNotMet()
    {
        AutomationConditionCheck check = Evaluate(SummerAfternoonUtc, [KioskInGame(KioskId, inGame: true)], kioskInGame: false);

        Assert.IsFalse(check.Passed);
        Assert.IsFalse(check.IsMisconfigured);
        Assert.AreEqual("Arena isn't in a game", check.Reason);
    }

    [TestMethod]
    public void ClientGameStatus_FailsClosed_ForAKioskInAnotherCompany()
    {
        // The engine runs unfiltered, so this is the only thing stopping a rule reading another
        // company's kiosk - even when that kiosk's state would satisfy the condition.
        AutomationConditionCheck check = Evaluate(SummerAfternoonUtc, [KioskInGame(KioskId, inGame: true)], kioskInGame: true, kioskCompanyId: 2);

        Assert.IsFalse(check.Passed);
        Assert.IsTrue(check.IsMisconfigured);
        StringAssert.Contains(check.Reason, "another company");
    }

    [TestMethod]
    public void ClientGameStatus_FailsClosed_ForAKioskThatNoLongerExists()
    {
        AutomationConditionCheck check = Evaluate(SummerAfternoonUtc, [KioskInGame(KioskId, inGame: false)], kioskExists: false);

        Assert.IsFalse(check.Passed);
        Assert.IsTrue(check.IsMisconfigured);
    }

    [TestMethod]
    public void Evaluate_FailsClosed_OnMalformedParameters()
    {
        AutomationConditionCheck check = Evaluate(SummerAfternoonUtc, [Condition(AutomationConditionType.TimeWindow, "{not json")]);

        Assert.IsFalse(check.Passed);
        Assert.IsTrue(check.IsMisconfigured);
    }

    [TestMethod]
    public void Evaluate_FailsClosed_OnAnUnknownConditionType()
    {
        AutomationConditionCheck check = Evaluate(SummerAfternoonUtc, [Condition((AutomationConditionType)99, "{}")]);

        Assert.IsFalse(check.Passed);
        Assert.IsTrue(check.IsMisconfigured);
    }

    [TestMethod]
    public void Evaluate_RequiresEveryCondition_AndReportsTheFirstFailureInOrder()
    {
        AutomationRuleCondition window = TimeWindow(new(16, 0), new(17, 0));
        window.SortOrder = 0;
        AutomationRuleCondition wrongDay = Days(DayOfWeek.Monday);
        wrongDay.SortOrder = 1;
        AutomationRuleCondition kiosk = KioskInGame(KioskId, inGame: true);
        kiosk.SortOrder = 2;

        AutomationConditionCheck check = Evaluate(SummerAfternoonUtc, [kiosk, wrongDay, window], kioskInGame: true);

        Assert.IsFalse(check.Passed, "One failing condition must stop the rule even when the others pass.");
        StringAssert.Contains(check.Reason, "Wednesday");
    }

    [TestMethod]
    public void Validate_RejectsAWindowWithTheSameStartAndEnd()
    {
        string json = AutomationConditionParameters.Serialize(new TimeWindowConditionParameters(new(9, 0), new(9, 0)));

        Assert.IsNotNull(AutomationConditionParameters.Validate(AutomationConditionType.TimeWindow, json));
    }

    [TestMethod]
    public void Validate_AcceptsWellFormedConditions()
    {
        Assert.IsNull(AutomationConditionParameters.Validate(AutomationConditionType.TimeWindow, TimeWindow(new(22, 0), new(2, 0)).ParametersJson));
        Assert.IsNull(AutomationConditionParameters.Validate(AutomationConditionType.DaysOfWeek, Days(DayOfWeek.Saturday, DayOfWeek.Sunday).ParametersJson));
        Assert.IsNull(AutomationConditionParameters.Validate(AutomationConditionType.ClientGameStatus, KioskInGame(KioskId, true).ParametersJson));
    }

    [TestMethod]
    public void Validate_RejectsAKioskConditionWithNoKiosk()
    {
        Assert.IsNotNull(AutomationConditionParameters.Validate(AutomationConditionType.ClientGameStatus, KioskInGame(Guid.Empty, true).ParametersJson));
    }
}
