using Lanyard.Application.Services.Policies;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Policies;

[TestClass]
public class PolicyAudienceServiceTests
{
    [TestMethod]
    public async Task Publish_AssignsEveryoneAtTheLocation_AndNobodyElse()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();

        Guid policyId = await w.PublishAsync();

        PolicyAssignment? amy = await w.AssignmentAsync(policyId, w.Amy);
        Assert.IsNotNull(amy);
        Assert.IsTrue(amy.IsActive);
        Assert.AreEqual(w.Company.Id, amy.CompanyId);
        Assert.AreEqual(PolicyWorld.Now, amy.OutstandingFrom);
        Assert.AreEqual(PolicyWorld.Now.AddDays(14), amy.DueDate);

        Assert.IsNull(await w.AssignmentAsync(policyId, w.Bob), "Bob works at Wisbech");
        Assert.IsNull(await w.AssignmentAsync(policyId, w.Cara), "Cara is another company's");
    }

    [TestMethod]
    public async Task CompanyWidePolicy_AssignsEveryLocationInTheCompanyOnly()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();

        Guid policyId = await w.PublishAsync(locationId: null);

        Assert.IsNotNull(await w.AssignmentAsync(policyId, w.Amy));
        Assert.IsNotNull(await w.AssignmentAsync(policyId, w.Bob));
        Assert.IsNull(await w.AssignmentAsync(policyId, w.Cara));
    }

    [TestMethod]
    public async Task PositionFilter_OnlyThoseHoldingThePosition()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        UserProfile chef = await SchedulingTestHelpers.SeedUserAsync(w.Options, w.Ipswich, "Chef");

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = chef.Id, StaffPositionId = w.Kitchen.Id, IsPrimary = true });
            await ctx.SaveChangesAsync();
        }

        Guid policyId = await w.PublishAsync(positionIds: [w.Kitchen.Id]);

        Assert.IsNotNull(await w.AssignmentAsync(policyId, chef));
        Assert.IsNull(await w.AssignmentAsync(policyId, w.Amy), "Amy holds no Kitchen position");
    }

    [TestMethod]
    public async Task Sync_NewStarter_IsAddedWithTheirOwnDeadline()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();

        w.Clock.Advance(TimeSpan.FromDays(30));
        UserProfile newStarter = await SchedulingTestHelpers.SeedUserAsync(w.Options, w.Ipswich, "Nina");

        Result<PolicySyncResult> sync = await w.Audience().SyncAsync(w.Company.Id);

        Assert.IsTrue(sync.IsSuccess, sync.Error);
        Assert.AreEqual(1, sync.Data!.Added);

        PolicyAssignment? assignment = await w.AssignmentAsync(policyId, newStarter);
        Assert.AreEqual(w.Clock.UtcNow, assignment!.OutstandingFrom);
        Assert.AreEqual(w.Clock.UtcNow.AddDays(14), assignment.DueDate);
    }

    [TestMethod]
    public async Task Sync_Leaver_IsDeactivatedButKeepsTheirAcknowledgement_AndComesBack()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();
        Guid versionId = await w.CurrentVersionIdAsync(policyId);
        await w.Acknowledgements().AcknowledgeAsync(versionId, w.Amy.Id, new(null, null, null));

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.UserLocationMemberships.RemoveRange(ctx.UserLocationMemberships.Where(x => x.UserId == w.Amy.Id));
            await ctx.SaveChangesAsync();
        }

        Result<PolicySyncResult> left = await w.Audience().SyncAsync(w.Company.Id);
        Assert.AreEqual(1, left.Data!.Deactivated);

        PolicyAssignment? gone = await w.AssignmentAsync(policyId, w.Amy);
        Assert.IsFalse(gone!.IsActive);
        Assert.AreEqual(1, gone.AcknowledgedVersionNumber);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = w.Amy.Id, LocationId = w.Ipswich.Id, CreateDate = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        Result<PolicySyncResult> back = await w.Audience().SyncAsync(w.Company.Id);
        Assert.AreEqual(1, back.Data!.Reactivated);

        PolicyAssignment? returned = await w.AssignmentAsync(policyId, w.Amy);
        Assert.IsTrue(returned!.IsActive);
        Assert.IsNull(returned.OutstandingFrom, "Their acknowledgement of the current version still counts");
    }

    [TestMethod]
    public async Task Sync_FeatureSwitchedOff_DoesNothing()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();
        UserProfile newStarter = await SchedulingTestHelpers.SeedUserAsync(w.Options, w.Ipswich, "Nina");

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = w.Company.Id, Feature = CompanyFeature.Policies, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        Result<PolicySyncResult> sync = await w.Audience().SyncAsync(w.Company.Id);

        Assert.IsTrue(sync.IsSuccess);
        Assert.AreEqual(0, sync.Data!.Added);
        Assert.IsNull(await w.AssignmentAsync(policyId, newStarter));
    }

    [TestMethod]
    public async Task Sync_IgnoresTheDeletedUserPlaceholder()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.Users.Add(new UserProfile { Id = ApplicationDbContext.SystemDeletedUserPlaceholderId, UserName = "deleted" });
            ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = ApplicationDbContext.SystemDeletedUserPlaceholderId, LocationId = w.Ipswich.Id, CreateDate = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        Guid policyId = await w.PublishAsync();

        await using ApplicationDbContext check = new(w.Options);
        Assert.IsFalse(await check.PolicyAssignments.AnyAsync(x => x.PolicyId == policyId && x.UserId == ApplicationDbContext.SystemDeletedUserPlaceholderId));
    }
}
