using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Policies;

// Acknowledgements are kept for 6 years and run through ScheduleRetention, the step both account
// delete paths (SecurityService.DeleteUserAsync and GdprService) already take.
[TestClass]
public class PolicyRetentionTests
{
    private const string Placeholder = ApplicationDbContext.SystemDeletedUserPlaceholderId;

    private static async Task<(PolicyWorld World, Guid PolicyId)> AcknowledgedAsync()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();
        await w.Acknowledgements().AcknowledgeAsync(await w.CurrentVersionIdAsync(policyId), w.Amy.Id, new("203.0.113.7", "Safari", "iPhone"));
        return (w, policyId);
    }

    [TestMethod]
    public async Task AccountDelete_KeepsTheAcknowledgementAndItsEvidence_UnderThePlaceholder()
    {
        (PolicyWorld w, _) = await AcknowledgedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            await ScheduleRetention.DetachUserAsync(ctx, w.Amy.Id, PolicyWorld.Now);
            await ctx.SaveChangesAsync();
        }

        await using ApplicationDbContext check = new(w.Options);
        PolicyAcknowledgement record = await check.PolicyAcknowledgements.SingleAsync();
        Assert.AreEqual(Placeholder, record.UserId);
        Assert.AreEqual("203.0.113.7", record.IpAddress);
        Assert.AreEqual("iPhone", record.DeviceLabel);
    }

    [TestMethod]
    public async Task GdprErasure_AlsoClearsIpAndDevice_AndThePublisherName()
    {
        (PolicyWorld w, Guid policyId) = await AcknowledgedAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            await ScheduleRetention.DetachUserAsync(ctx, w.Amy.Id, PolicyWorld.Now, gdprErasure: true);
            await ScheduleRetention.DetachUserAsync(ctx, "admin", PolicyWorld.Now, gdprErasure: true);
            await ctx.SaveChangesAsync();
        }

        await using ApplicationDbContext check = new(w.Options);
        PolicyAcknowledgement record = await check.PolicyAcknowledgements.SingleAsync();
        Assert.AreEqual(Placeholder, record.UserId);
        Assert.IsNull(record.IpAddress);
        Assert.IsNull(record.UserAgent);
        Assert.IsNull(record.DeviceLabel);
        Assert.IsNotNull(record.AcknowledgedAtUtc);

        Assert.IsNull((await check.Policies.SingleAsync(x => x.Id == policyId)).CreatedByUserId);
        Assert.IsNull((await check.PolicyVersions.SingleAsync()).PublishedByUserId);
    }

    [TestMethod]
    public async Task FailedDelete_RestorePutsItAllBack()
    {
        (PolicyWorld w, _) = await AcknowledgedAsync();
        ScheduleRetention.Snapshot snapshot;

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            snapshot = await ScheduleRetention.DetachUserAsync(ctx, w.Amy.Id, PolicyWorld.Now);
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(1, snapshot.Policies.Count);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            await ScheduleRetention.RestoreAsync(ctx, snapshot);
            await ctx.SaveChangesAsync();
        }

        await using ApplicationDbContext check = new(w.Options);
        Assert.AreEqual(w.Amy.Id, (await check.PolicyAcknowledgements.SingleAsync()).UserId);
    }
}
