using Lanyard.Application.Services.Notifications;
using Lanyard.Application.Services.Policies;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Policies;

[TestClass]
public class PolicyReminderTests
{
    // PolicyWorld.Now is 12:00 UTC, which is 13:00 UK time in October: inside the sending hours.
    private static PolicyReminderHostedService Sweeper(PolicyWorld w) =>
        new(null!, w.Clock, NullLogger<PolicyReminderHostedService>.Instance);

    private static Task<int> SweepAsync(PolicyWorld w) =>
        Sweeper(w).RunSweepAsync(SchedulingTestHelpers.GetFactory(w.Options), w.Audience(), w.Reminders());

    private static List<NotificationJob> For(PolicyWorld w, UserProfile user) =>
        [.. w.Notifications.Jobs.Where(x => x.UserId == user.Id && x.Topic == NotificationTopic.PolicyAcknowledgement)];

    [TestMethod]
    public async Task Publish_TellsEveryoneItsFor_Once()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();

        Guid policyId = await w.PublishAsync();

        NotificationJob job = For(w, w.Amy).Single();
        PolicyReminderPayload payload = (PolicyReminderPayload)job.Payload;
        Assert.AreEqual(PolicyReminderKind.New, payload.Kind);
        Assert.AreEqual(policyId, payload.PolicyId);
        Assert.AreEqual(w.Ipswich.Id, payload.LocationId);
        Assert.AreEqual(0, For(w, w.Bob).Count, "Bob is at Wisbech");

        await SweepAsync(w);
        Assert.AreEqual(1, For(w, w.Amy).Count, "Nothing more until it's overdue");
    }

    [TestMethod]
    public async Task Overdue_ThenWeekly_AtMostThree()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        await w.PublishAsync(acknowledgeWithinDays: 3);

        w.Clock.Advance(TimeSpan.FromDays(4));
        await SweepAsync(w);
        await SweepAsync(w);
        Assert.AreEqual(2, For(w, w.Amy).Count, "One overdue reminder, not one per sweep");
        Assert.AreEqual(PolicyReminderKind.Overdue, ((PolicyReminderPayload)For(w, w.Amy)[1].Payload).Kind);

        w.Clock.Advance(TimeSpan.FromDays(3));
        await SweepAsync(w);
        Assert.AreEqual(2, For(w, w.Amy).Count, "Not a week yet");

        w.Clock.Advance(TimeSpan.FromDays(5));
        await SweepAsync(w);
        Assert.AreEqual(3, For(w, w.Amy).Count);

        w.Clock.Advance(TimeSpan.FromDays(14));
        await SweepAsync(w);
        Assert.AreEqual(3, For(w, w.Amy).Count, "Three is the most anyone gets");
    }

    [TestMethod]
    public async Task Acknowledged_GetsNoMoreReminders()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync(acknowledgeWithinDays: 3);
        await w.Acknowledgements().AcknowledgeAsync(await w.CurrentVersionIdAsync(policyId), w.Amy.Id, new(null, null, null));

        w.Clock.Advance(TimeSpan.FromDays(10));
        await SweepAsync(w);

        Assert.AreEqual(1, For(w, w.Amy).Count);
    }

    [TestMethod]
    public async Task NewVersionNeedingReacknowledgement_StartsAFreshRoundAsReadAgain()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();
        await w.Acknowledgements().AcknowledgeAsync(await w.CurrentVersionIdAsync(policyId), w.Amy.Id, new(null, null, null));

        w.Clock.Advance(TimeSpan.FromDays(30));
        await w.Policies().PublishVersionAsync(policyId, PolicyWorld.Pdf(), "New assembly point", true, w.AdminScope, "admin");

        Assert.AreEqual(2, For(w, w.Amy).Count);
        Assert.AreEqual(PolicyReminderKind.ReadAgain, ((PolicyReminderPayload)For(w, w.Amy)[1].Payload).Kind);
    }

    [TestMethod]
    public async Task Sweep_PicksUpANewStarter_AndTellsThem()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        await w.PublishAsync();
        UserProfile newStarter = await SchedulingTestHelpers.SeedUserAsync(w.Options, w.Ipswich, "Nina");

        int sent = await SweepAsync(w);

        Assert.AreEqual(1, sent);
        Assert.AreEqual(1, For(w, newStarter).Count);
    }

    [TestMethod]
    public async Task Sweep_OutsideDaytime_SyncsButSendsNothing()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        await w.PublishAsync();
        UserProfile newStarter = await SchedulingTestHelpers.SeedUserAsync(w.Options, w.Ipswich, "Nina");
        w.Clock.UtcNow = new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc);

        int sent = await SweepAsync(w);

        Assert.AreEqual(0, sent);
        Assert.AreEqual(0, For(w, newStarter).Count);
        Assert.IsNotNull(await w.AssignmentAsync((await LoadPolicyIdAsync(w)), newStarter), "The audience is still brought up to date overnight");
    }

    [TestMethod]
    public async Task Sweep_SkipsCompaniesWithPoliciesSwitchedOff()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        await w.PublishAsync(acknowledgeWithinDays: 1);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = w.Company.Id, Feature = CompanyFeature.Policies, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        w.Clock.Advance(TimeSpan.FromDays(3));
        await SweepAsync(w);

        Assert.AreEqual(1, For(w, w.Amy).Count, "Only the publish notice, from before it was switched off");
    }

    [TestMethod]
    public async Task ManualReminder_RemindsOutstanding_ButNotTwiceInADay()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();
        PolicyReminderService reminders = w.Reminders();

        Result<int> tooSoon = await reminders.RemindOutstandingAsync(policyId, w.AdminScope);
        Assert.AreEqual(0, tooSoon.Data, "Amy was told when it was published");

        w.Clock.Advance(TimeSpan.FromDays(2));
        Result<int> nudge = await reminders.RemindOutstandingAsync(policyId, w.AdminScope);
        Result<int> again = await reminders.RemindOutstandingAsync(policyId, w.AdminScope);

        Assert.AreEqual(1, nudge.Data);
        Assert.AreEqual(0, again.Data);
        Assert.AreEqual(2, For(w, w.Amy).Count);
    }

    [TestMethod]
    public async Task ManualReminder_ManagerElsewhereOrStaff_AreRefused()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();

        Assert.IsFalse((await w.Reminders().RemindOutstandingAsync(policyId, w.ManagerAt(w.Wisbech))).IsSuccess);
        Assert.IsFalse((await w.Reminders().RemindOutstandingAsync(policyId, w.StaffAt(w.Ipswich))).IsSuccess);
    }

    [TestMethod]
    public void Notices_HaveWordingForEveryKind_AndPushReplacesPerPolicy()
    {
        Guid policyId = Guid.NewGuid();

        foreach (PolicyReminderKind kind in System.Enum.GetValues<PolicyReminderKind>())
        {
            PolicyReminderPayload payload = new(1, policyId, "Fire policy", 2, "30 Oct", kind);
            PushContent push = PushContentBuilder.Build(payload, new DateOnly(2026, 10, 19));

            StringAssert.Contains(push.Title, "Fire policy");
            Assert.AreEqual($"/policies/{policyId}", push.Url);
            Assert.AreEqual($"policy-{policyId:N}", push.Tag);
        }

        Assert.AreEqual(NotificationTopicGroup.Policies, NotificationTopics.Get(NotificationTopic.PolicyAcknowledgement).Group);
    }

    private static async Task<Guid> LoadPolicyIdAsync(PolicyWorld w)
    {
        await using ApplicationDbContext ctx = new(w.Options);
        return (await ctx.Policies.SingleAsync()).Id;
    }
}
