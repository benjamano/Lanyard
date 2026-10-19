using Lanyard.Application.Services.Policies;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Policies;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Policies;

[TestClass]
public class PolicyAcknowledgementServiceTests
{
    private static readonly PolicyAcknowledgementEvidence Phone = new("203.0.113.7", "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)", "iPhone");

    [TestMethod]
    public async Task Acknowledge_StoresTheEvidence_AndClearsTheRequirement()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();
        Guid versionId = await w.CurrentVersionIdAsync(policyId);
        w.Clock.Advance(TimeSpan.FromHours(3));

        Result<PolicyAcknowledgementResult> result = await w.Acknowledgements().AcknowledgeAsync(versionId, w.Amy.Id, Phone);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse(result.Data!.AlreadyAcknowledged);

        await using ApplicationDbContext ctx = new(w.Options);
        PolicyAcknowledgement record = await ctx.PolicyAcknowledgements.SingleAsync();
        PolicyVersion version = await ctx.PolicyVersions.SingleAsync(x => x.Id == versionId);

        Assert.AreEqual(w.Amy.Id, record.UserId);
        Assert.AreEqual(versionId, record.PolicyVersionId);
        Assert.AreEqual(w.Company.Id, record.CompanyId);
        Assert.AreEqual(w.Clock.UtcNow, record.AcknowledgedAtUtc);
        Assert.AreEqual("I have read and understood Fire policy (version 1).", record.StatementText);
        Assert.AreEqual(version.FileSha256, record.FileSha256);
        Assert.AreEqual("203.0.113.7", record.IpAddress);
        Assert.AreEqual("iPhone", record.DeviceLabel);

        PolicyAssignment assignment = (await w.AssignmentAsync(policyId, w.Amy))!;
        Assert.AreEqual(1, assignment.AcknowledgedVersionNumber);
        Assert.AreEqual(record.Id, assignment.LastAcknowledgementId);
        Assert.IsNull(assignment.OutstandingFrom);
    }

    [TestMethod]
    public async Task Acknowledge_Twice_ReturnsTheFirstRecord()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid versionId = await w.CurrentVersionIdAsync(await w.PublishAsync());
        PolicyAcknowledgementService service = w.Acknowledgements();

        Result<PolicyAcknowledgementResult> first = await service.AcknowledgeAsync(versionId, w.Amy.Id, Phone);
        Result<PolicyAcknowledgementResult> second = await service.AcknowledgeAsync(versionId, w.Amy.Id, Phone);

        Assert.IsTrue(second.Data!.AlreadyAcknowledged);
        Assert.AreEqual(first.Data!.AcknowledgementId, second.Data.AcknowledgementId);

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.AreEqual(1, await ctx.PolicyAcknowledgements.CountAsync());
    }

    [TestMethod]
    public async Task Acknowledge_SomeoneItIsntAssignedTo_IsRefused()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid versionId = await w.CurrentVersionIdAsync(await w.PublishAsync());

        Result<PolicyAcknowledgementResult> result = await w.Acknowledgements().AcknowledgeAsync(versionId, w.Bob.Id, Phone);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Acknowledge_AnOlderVersion_IsRefused()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();
        Guid v1 = await w.CurrentVersionIdAsync(policyId);
        await w.Policies().PublishVersionAsync(policyId, PolicyWorld.Pdf(), null, true, w.AdminScope, "admin");

        Result<PolicyAcknowledgementResult> result = await w.Acknowledgements().AcknowledgeAsync(v1, w.Amy.Id, Phone);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "newer version");
    }

    [TestMethod]
    public async Task Acknowledge_AnotherCompanysVersion_IsRefused()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid versionId = await w.CurrentVersionIdAsync(await w.PublishAsync());

        Result<PolicyAcknowledgementResult> result = await w.Acknowledgements(w.Tenant(w.OtherCompany.Id)).AcknowledgeAsync(versionId, w.Amy.Id, Phone);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Acknowledge_TruncatesOversizedEvidence()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid versionId = await w.CurrentVersionIdAsync(await w.PublishAsync());

        await w.Acknowledgements().AcknowledgeAsync(versionId, w.Amy.Id, new("1.2.3.4", new string('x', 2000), new string('d', 500)));

        await using ApplicationDbContext ctx = new(w.Options);
        PolicyAcknowledgement record = await ctx.PolicyAcknowledgements.SingleAsync();
        Assert.AreEqual(512, record.UserAgent!.Length);
        Assert.AreEqual(100, record.DeviceLabel!.Length);
    }

    [TestMethod]
    public async Task Interval_ReopensBeforeExpiry_AndCanBeAcknowledgedAgain()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync(reacknowledgeEveryMonths: 12);
        Guid versionId = await w.CurrentVersionIdAsync(policyId);
        PolicyAcknowledgementService service = w.Acknowledgements();
        await service.AcknowledgeAsync(versionId, w.Amy.Id, Phone);

        Assert.AreEqual(PolicyStatus.UpToDate, (await service.GetMyPolicyAsync(w.Amy.Id, policyId)).Data!.Status);

        w.Clock.UtcNow = PolicyWorld.Now.AddMonths(12).AddDays(-7);
        Assert.AreEqual(PolicyStatus.Outstanding, (await service.GetMyPolicyAsync(w.Amy.Id, policyId)).Data!.Status);

        Result<PolicyAcknowledgementResult> again = await service.AcknowledgeAsync(versionId, w.Amy.Id, Phone);
        Assert.IsFalse(again.Data!.AlreadyAcknowledged);
        Assert.AreEqual(PolicyStatus.UpToDate, (await service.GetMyPolicyAsync(w.Amy.Id, policyId)).Data!.Status);

        await using ApplicationDbContext ctx = new(w.Options);
        Assert.AreEqual(2, await ctx.PolicyAcknowledgements.CountAsync());
    }

    [TestMethod]
    public async Task MyPolicies_OverdueFirst_AndOnlyMine()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        await w.PublishAsync("Handbook", acknowledgeWithinDays: 30);
        await w.PublishAsync("Fire policy", acknowledgeWithinDays: 3);
        await w.PublishAsync("Wisbech only", locationId: w.Wisbech.Id);
        w.Clock.Advance(TimeSpan.FromDays(5));

        Result<List<MyPolicyItem>> result = await w.Acknowledgements().GetMyPoliciesAsync(w.Amy.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(new[] { "Fire policy", "Handbook" }, result.Data!.Select(x => x.Name).ToArray());
        Assert.AreEqual(PolicyStatus.Overdue, result.Data[0].Status);
        Assert.AreEqual(PolicyStatus.Outstanding, result.Data[1].Status);
        Assert.IsNotNull(result.Data[0].CurrentVersionId);
    }

    [TestMethod]
    public async Task PoliciesForPerson_ManagerAtTheirLocation_YesElsewhere_No()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        await w.PublishAsync();
        PolicyAcknowledgementService service = w.Acknowledgements();

        Result<List<MyPolicyItem>> sameLocation = await service.GetPoliciesForPersonAsync(w.Amy.Id, "mgr", w.ManagerAt(w.Ipswich));
        Result<List<MyPolicyItem>> otherLocation = await service.GetPoliciesForPersonAsync(w.Amy.Id, "mgr", w.ManagerAt(w.Wisbech));
        Result<List<MyPolicyItem>> colleague = await service.GetPoliciesForPersonAsync(w.Amy.Id, w.Bob.Id, w.StaffAt(w.Ipswich));

        Assert.AreEqual(1, sameLocation.Data!.Count);
        Assert.IsFalse(otherLocation.IsSuccess);
        Assert.IsFalse(colleague.IsSuccess);
    }

    [TestMethod]
    public async Task History_EvidenceOnlyForAdmins_AndManagersOnlyForTheirLocation()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid versionId = await w.CurrentVersionIdAsync(await w.PublishAsync());
        PolicyAcknowledgementService service = w.Acknowledgements();
        await service.AcknowledgeAsync(versionId, w.Amy.Id, Phone);

        Result<List<PolicyAcknowledgementRecord>> own = await service.GetHistoryAsync(w.Amy.Id, w.Amy.Id, w.StaffAt(w.Ipswich));
        Result<List<PolicyAcknowledgementRecord>> admin = await service.GetHistoryAsync(w.Amy.Id, "admin", w.AdminScope);
        Result<List<PolicyAcknowledgementRecord>> otherManager = await service.GetHistoryAsync(w.Amy.Id, "mgr", w.ManagerAt(w.Wisbech));
        Result<List<PolicyAcknowledgementRecord>> otherStaff = await service.GetHistoryAsync(w.Amy.Id, w.Bob.Id, w.StaffAt(w.Wisbech));

        Assert.IsNull(own.Data!.Single().IpAddress);
        Assert.AreEqual("203.0.113.7", admin.Data!.Single().IpAddress);
        Assert.IsFalse(otherManager.IsSuccess);
        Assert.IsFalse(otherStaff.IsSuccess);
    }
}
