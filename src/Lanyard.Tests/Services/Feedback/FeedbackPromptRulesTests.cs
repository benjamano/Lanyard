using Lanyard.Application.Services.Feedback;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Feedback;

// When "How would you rate Lanyard?" appears.
[TestClass]
public class FeedbackPromptRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LongAgo = Now.AddDays(-365);

    [TestMethod]
    public void ALongStandingUser_WhoHasNeverBeenAsked_IsAsked()
    {
        Assert.IsTrue(FeedbackPromptRules.ShouldPrompt(false, LongAgo, null, null, Now));
    }

    [TestMethod]
    public void AnAccountWithNoStartDate_CountsAsLongStanding()
    {
        Assert.IsTrue(FeedbackPromptRules.ShouldPrompt(false, null, null, null, Now));
    }

    [TestMethod]
    public void SomeoneWhoOptedOut_IsNeverAsked()
    {
        Assert.IsFalse(FeedbackPromptRules.ShouldPrompt(true, LongAgo, null, null, Now));
    }

    [TestMethod]
    [DataRow(6, false)]
    [DataRow(7, true)]
    public void ANewAccount_WaitsAWeek(int daysOld, bool expected)
    {
        Assert.AreEqual(expected, FeedbackPromptRules.ShouldPrompt(false, Now.AddDays(-daysOld), null, null, Now));
    }

    [TestMethod]
    [DataRow(29, false)]
    [DataRow(30, true)]
    public void AfterARating_TheyAreAskedAgainAMonthLater(int daysSinceRating, bool expected)
    {
        Assert.AreEqual(expected, FeedbackPromptRules.ShouldPrompt(false, LongAgo, Now.AddDays(-daysSinceRating), Now.AddDays(-daysSinceRating), Now));
    }

    [TestMethod]
    [DataRow(6, false)]
    [DataRow(7, true)]
    public void AfterNotNow_TheyAreAskedAgainAWeekLater(int daysSincePrompt, bool expected)
    {
        Assert.AreEqual(expected, FeedbackPromptRules.ShouldPrompt(false, LongAgo, Now.AddDays(-daysSincePrompt), null, Now));
    }
}
