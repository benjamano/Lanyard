using Lanyard.App.Components.Clients;
using Lanyard.Application.Services;
using Lanyard.Shared.Enum;
using Lanyard.Shared.Kiosk;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Components.Clients;

[TestClass]
public class KioskSelfTestDisplayTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 14, 0, 0, DateTimeKind.Utc);

    private static KioskSelfTestRunView Run(ClientSelfTestStatus status = ClientSelfTestStatus.Completed, params SelfTestOutcome[] outcomes)
    {
        return new KioskSelfTestRunView
        {
            Status = status,
            RequestedAtUtc = Now.AddDays(-2),
            Groups =
            [
                new KioskSelfTestGroupView
                {
                    Group = SelfTestCheckKeys.GroupAudio,
                    Checks = outcomes.Select(o => new KioskSelfTestCheckView { Key = SelfTestCheckKeys.AudioDevice, Outcome = o }).ToList()
                }
            ]
        };
    }

    [TestMethod]
    public void Summary_CountsProblemsAndWarnings()
    {
        Assert.AreEqual("Everything passed", KioskSelfTestDisplay.Summary(Run(outcomes: [SelfTestOutcome.Pass, SelfTestOutcome.Skipped, SelfTestOutcome.NotConfigured])));
        Assert.AreEqual("1 problem", KioskSelfTestDisplay.Summary(Run(outcomes: [SelfTestOutcome.Pass, SelfTestOutcome.Fail])));
        Assert.AreEqual("2 problems and 1 thing to check", KioskSelfTestDisplay.Summary(Run(outcomes: [SelfTestOutcome.Fail, SelfTestOutcome.Fail, SelfTestOutcome.Warn])));
        Assert.AreEqual("2 things to check", KioskSelfTestDisplay.Summary(Run(outcomes: [SelfTestOutcome.Warn, SelfTestOutcome.Warn])));
        Assert.IsNull(KioskSelfTestDisplay.Summary(Run(ClientSelfTestStatus.TimedOut)), "No checklist, so the run's error is shown instead");
    }

    [TestMethod]
    public void SummaryIntent_FollowsTheWorstOutcome()
    {
        Assert.AreEqual(MessageBarIntent.Success, KioskSelfTestDisplay.SummaryIntent(Run(outcomes: [SelfTestOutcome.Pass, SelfTestOutcome.Skipped])));
        Assert.AreEqual(MessageBarIntent.Warning, KioskSelfTestDisplay.SummaryIntent(Run(outcomes: [SelfTestOutcome.Pass, SelfTestOutcome.Warn])));
        Assert.AreEqual(MessageBarIntent.Error, KioskSelfTestDisplay.SummaryIntent(Run(outcomes: [SelfTestOutcome.Warn, SelfTestOutcome.Fail])));
        Assert.AreEqual(MessageBarIntent.Error, KioskSelfTestDisplay.SummaryIntent(Run(ClientSelfTestStatus.KioskTooOld)));
    }

    [TestMethod]
    public void ShortResult_ExplainsRunsWithoutAChecklist()
    {
        Assert.AreEqual("Kiosk didn't reply", KioskSelfTestDisplay.ShortResult(Run(ClientSelfTestStatus.TimedOut)));
        Assert.AreEqual("Kiosk needs updating", KioskSelfTestDisplay.ShortResult(Run(ClientSelfTestStatus.KioskTooOld)));
        Assert.AreEqual("Couldn't run", KioskSelfTestDisplay.ShortResult(Run(ClientSelfTestStatus.Failed)));
    }

    [TestMethod]
    public void RequestedBy_HasAPlaceholderOnceTheAccountIsErased()
    {
        Assert.AreEqual("Ada Min", KioskSelfTestDisplay.RequestedBy(new KioskSelfTestRunView { RequestedByName = "Ada Min" }));
        Assert.AreEqual("a former member of staff", KioskSelfTestDisplay.RequestedBy(new KioskSelfTestRunView()));
    }

    [TestMethod]
    public void AskSomeoneNearby_OnlyForThingsTheKioskCantSeeOrHear()
    {
        Assert.IsNotNull(KioskSelfTestDisplay.AskSomeoneNearby(new KioskSelfTestCheckView { Key = SelfTestCheckKeys.AudioTone, Outcome = SelfTestOutcome.Pass }));
        Assert.IsNotNull(KioskSelfTestDisplay.AskSomeoneNearby(new KioskSelfTestCheckView { Key = SelfTestCheckKeys.DmxFlash, Outcome = SelfTestOutcome.Pass }));
        Assert.IsNull(KioskSelfTestDisplay.AskSomeoneNearby(new KioskSelfTestCheckView { Key = SelfTestCheckKeys.AudioTone, Outcome = SelfTestOutcome.Skipped }));
        Assert.IsNull(KioskSelfTestDisplay.AskSomeoneNearby(new KioskSelfTestCheckView { Key = SelfTestCheckKeys.DmxPort, Outcome = SelfTestOutcome.Pass }));
    }

    [TestMethod]
    public void HealthLine()
    {
        Assert.AreEqual("No self-test has been run on this kiosk.", KioskSelfTestDisplay.HealthLine(null, Now));
        Assert.AreEqual("Last self-test 2 days ago: 1 problem.", KioskSelfTestDisplay.HealthLine(Run(outcomes: [SelfTestOutcome.Fail]), Now));
        Assert.AreEqual("Last self-test 2 days ago: kiosk didn't reply.", KioskSelfTestDisplay.HealthLine(Run(ClientSelfTestStatus.TimedOut), Now));
    }

    [TestMethod]
    public void OutcomeLabelsAndClasses_CoverEveryOutcome()
    {
        foreach (SelfTestOutcome outcome in Enum.GetValues<SelfTestOutcome>())
        {
            Assert.AreNotEqual("Unknown", KioskSelfTestDisplay.OutcomeLabel(outcome));
            StringAssert.StartsWith(KioskSelfTestDisplay.OutcomeClass(outcome), "self-test-check-");
        }
    }
}
