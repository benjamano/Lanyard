using Lanyard.Application.Services;
using Lanyard.Application.Services.Policies;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Policies;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Policies;

[TestClass]
public class PolicyServiceTests
{
    private static PolicySaveRequest Request(int? locationId, string name = "Fire policy", Guid? id = null, int days = 14) =>
        new(id, name, "Read before your first shift", locationId, [], days, null);

    // ---- Who can do what ----

    [TestMethod]
    public async Task Save_SystemCaller_IsRefused()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();

        Result<Guid> result = await w.Policies(w.Tenant(isSystem: true)).SavePolicyAsync(Request(w.Ipswich.Id), w.AdminScope, "admin");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Save_PlainStaff_IsRefused()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();

        Result<Guid> result = await w.Policies().SavePolicyAsync(Request(w.Ipswich.Id), w.StaffAt(w.Ipswich), w.Amy.Id);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Save_Manager_CanOnlyUseTheirOwnLocation()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        PolicyService service = w.Policies();

        Assert.IsTrue((await service.SavePolicyAsync(Request(w.Ipswich.Id), w.ManagerAt(w.Ipswich), "mgr")).IsSuccess);
        Assert.IsFalse((await service.SavePolicyAsync(Request(w.Wisbech.Id), w.ManagerAt(w.Ipswich), "mgr")).IsSuccess);
        Assert.IsFalse((await service.SavePolicyAsync(Request(null), w.ManagerAt(w.Ipswich), "mgr")).IsSuccess, "Company-wide is Admin-only");
    }

    [TestMethod]
    public async Task Save_Admin_CannotUseAnotherCompanysLocation()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();

        Result<Guid> result = await w.Policies().SavePolicyAsync(Request(w.OtherLocation.Id), w.AdminScope, "admin");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Save_Manager_CanSeeButNotEditACompanyWidePolicy()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync(locationId: null);
        PolicyService service = w.Policies();

        Result<PolicyDetail> detail = await service.GetPolicyAsync(policyId, w.ManagerAt(w.Ipswich));
        Assert.IsTrue(detail.IsSuccess);
        Assert.IsFalse(detail.Data!.CanManage);

        Result<Guid> edit = await service.SavePolicyAsync(Request(null, "Renamed", policyId), w.ManagerAt(w.Ipswich), "mgr");
        Assert.IsFalse(edit.IsSuccess);

        Result<PolicyVersionInfo> publish = await service.PublishVersionAsync(policyId, PolicyWorld.Pdf(), null, true, w.ManagerAt(w.Ipswich), "mgr");
        Assert.IsFalse(publish.IsSuccess);
    }

    [TestMethod]
    public async Task GetPolicies_ManagerSeesOwnLocationAndCompanyWide_CountingOnlyTheirPeople()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        await w.PublishAsync("Ipswich fire policy");
        await w.PublishAsync("Wisbech fire policy", locationId: w.Wisbech.Id);
        await w.PublishAsync("Handbook", locationId: null);

        Result<List<PolicySummary>> result = await w.Policies().GetPoliciesAsync(w.ManagerAt(w.Ipswich));

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEquivalent(new[] { "Handbook", "Ipswich fire policy" }, result.Data!.Select(x => x.Name).ToArray());

        PolicySummary handbook = result.Data.Single(x => x.Name == "Handbook");
        Assert.AreEqual(1, handbook.AudienceCount, "Only Amy is at Ipswich; Bob is counted at Wisbech");
        Assert.AreEqual(1, handbook.OutstandingCount);
        Assert.AreEqual("Whole company", handbook.ScopeName);
    }

    [TestMethod]
    public async Task Save_RejectsOutOfRangeDeadline()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();

        Result<Guid> result = await w.Policies().SavePolicyAsync(Request(w.Ipswich.Id, days: 0), w.AdminScope, "admin");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task EditorOptions_AdminGetsEveryLocationAndCompanyWide_ManagerOnlyTheirOwn()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();

        Result<PolicyEditorOptions> admin = await w.Policies().GetEditorOptionsAsync(w.AdminScope);
        Result<PolicyEditorOptions> manager = await w.Policies().GetEditorOptionsAsync(w.ManagerAt(w.Wisbech));

        Assert.IsTrue(admin.Data!.CanChooseCompanyWide);
        CollectionAssert.AreEquivalent(new[] { w.Ipswich.Id, w.Wisbech.Id }, admin.Data.Locations.Select(x => x.Id).ToArray());
        Assert.AreEqual(w.Kitchen.Id, admin.Data.Positions.Single().Id);

        Assert.IsFalse(manager.Data!.CanChooseCompanyWide);
        Assert.AreEqual(w.Wisbech.Id, manager.Data.Locations.Single().Id);
        Assert.AreEqual(w.Wisbech.Id, manager.Data.DefaultLocationId);
    }

    // ---- Publishing ----

    [TestMethod]
    public async Task Publish_UploadsARestrictedFile_HashesIt_AndNumbersVersions()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();

        Result<PolicyVersionInfo> second = await w.Policies().PublishVersionAsync(policyId, PolicyWorld.Pdf(), "Typo", false, w.AdminScope, "admin");

        Assert.IsTrue(second.IsSuccess, second.Error);
        Assert.AreEqual(2, second.Data!.VersionNumber);
        Assert.IsFalse(second.Data.RequiresReacknowledgement);
        Assert.IsTrue(w.UploadOptions.All(x => x.IsRestricted && !x.AddAudioToMusicLibrary));

        await using ApplicationDbContext ctx = new(w.Options);
        Policy policy = await ctx.Policies.SingleAsync(x => x.Id == policyId);
        Assert.AreEqual(2, policy.CurrentVersionNumber);
        Assert.AreEqual(1, policy.RequiredVersionNumber, "A typo fix leaves v1 as the one that counts");

        PolicyVersion v1 = await ctx.PolicyVersions.SingleAsync(x => x.PolicyId == policyId && x.VersionNumber == 1);
        Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(PolicyWorld.PdfBytes)), v1.FileSha256);
        Assert.IsTrue(v1.RequiresReacknowledgement);
    }

    [TestMethod]
    public async Task Publish_FirstVersion_AlwaysRequiresAcknowledgement()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        PolicyService service = w.Policies();
        Guid policyId = (await service.SavePolicyAsync(Request(w.Ipswich.Id), w.AdminScope, "admin")).Data;

        Result<PolicyVersionInfo> first = await service.PublishVersionAsync(policyId, PolicyWorld.Pdf(), null, requiresReacknowledgement: false, w.AdminScope, "admin");

        Assert.IsTrue(first.Data!.RequiresReacknowledgement);
        Assert.IsNotNull((await w.AssignmentAsync(policyId, w.Amy))!.OutstandingFrom);
    }

    [TestMethod]
    public async Task Publish_RejectsFilesThatArentPdfs()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        PolicyService service = w.Policies();
        Guid policyId = (await service.SavePolicyAsync(Request(w.Ipswich.Id), w.AdminScope, "admin")).Data;

        Result<PolicyVersionInfo> wrongExtension = await service.PublishVersionAsync(policyId, PolicyWorld.Pdf("policy.docx"), null, true, w.AdminScope, "admin");
        Result<PolicyVersionInfo> renamed = await service.PublishVersionAsync(policyId, PolicyWorld.Pdf("policy.pdf", "PK\u0003\u0004 not a pdf"u8.ToArray()), null, true, w.AdminScope, "admin");
        Result<PolicyVersionInfo> empty = await service.PublishVersionAsync(policyId, PolicyWorld.Pdf("policy.pdf", []), null, true, w.AdminScope, "admin");

        Assert.IsFalse(wrongExtension.IsSuccess);
        Assert.IsFalse(renamed.IsSuccess);
        Assert.IsFalse(empty.IsSuccess);
        w.Files.Verify(x => x.UploadFileAsync(It.IsAny<IFormFile>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<FileUploadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Publish_RequiringReacknowledgement_MakesEveryoneOutstandingAgain()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();
        await w.Acknowledgements().AcknowledgeAsync(await w.CurrentVersionIdAsync(policyId), w.Amy.Id, new(null, null, null));
        Assert.IsNull((await w.AssignmentAsync(policyId, w.Amy))!.OutstandingFrom);

        w.Clock.Advance(TimeSpan.FromDays(60));
        await w.Policies().PublishVersionAsync(policyId, PolicyWorld.Pdf(), "New assembly point", true, w.AdminScope, "admin");

        PolicyAssignment amy = (await w.AssignmentAsync(policyId, w.Amy))!;
        Assert.AreEqual(w.Clock.UtcNow, amy.OutstandingFrom);
        Assert.AreEqual(w.Clock.UtcNow.AddDays(14), amy.DueDate);
    }

    [TestMethod]
    public async Task Deactivate_StopsAskingAnyone()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();

        Result<bool> result = await w.Policies().DeactivatePolicyAsync(policyId, w.AdminScope);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse((await w.AssignmentAsync(policyId, w.Amy))!.IsActive);
        Assert.AreEqual(0, (await w.Acknowledgements().GetMyPoliciesAsync(w.Amy.Id)).Data!.Count);
    }

    [TestMethod]
    public async Task Status_OverdueFirst_ManagerSeesOnlyTheirLocation()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync(locationId: null);
        w.Clock.Advance(TimeSpan.FromDays(20));

        Result<List<PolicyPersonStatus>> admin = await w.Policies().GetPolicyStatusAsync(policyId, w.AdminScope);
        Result<List<PolicyPersonStatus>> manager = await w.Policies().GetPolicyStatusAsync(policyId, w.ManagerAt(w.Wisbech));

        Assert.AreEqual(2, admin.Data!.Count);
        Assert.IsTrue(admin.Data.All(x => x.Status == PolicyStatus.Overdue));
        Assert.AreEqual(w.Bob.Id, manager.Data!.Single().UserId);
    }

    // ---- Reading the PDF ----

    [TestMethod]
    public async Task OpenFile_AssignedStaff_ManagerInScope_AndAdmin_AreAllowed()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();
        Guid versionId = await w.CurrentVersionIdAsync(policyId);
        PolicyService service = w.Policies();

        Assert.IsTrue((await service.OpenVersionFileAsync(versionId, w.Amy.Id, w.StaffAt(w.Ipswich), null)).IsSuccess);
        Assert.IsTrue((await service.OpenVersionFileAsync(versionId, "mgr", w.ManagerAt(w.Ipswich), null)).IsSuccess);
        Assert.IsTrue((await service.OpenVersionFileAsync(versionId, "admin", w.AdminScope, null)).IsSuccess);
    }

    [TestMethod]
    public async Task OpenFile_UnassignedStaff_OtherManager_OtherCompany_AndKiosk_AreRefused()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();
        Guid versionId = await w.CurrentVersionIdAsync(policyId);

        Assert.IsFalse((await w.Policies().OpenVersionFileAsync(versionId, w.Bob.Id, w.StaffAt(w.Wisbech), null)).IsSuccess);
        Assert.IsFalse((await w.Policies().OpenVersionFileAsync(versionId, "mgr", w.ManagerAt(w.Wisbech), null)).IsSuccess);
        Assert.IsFalse((await w.Policies(w.Tenant(w.OtherCompany.Id)).OpenVersionFileAsync(versionId, w.Cara.Id, w.AdminScope, null)).IsSuccess);
        Assert.IsFalse((await w.Policies(w.Tenant(isSystem: true)).OpenVersionFileAsync(versionId, "kiosk", w.AdminScope, null)).IsSuccess);

        w.Files.Verify(x => x.OpenRestrictedFileContentAsync(It.IsAny<Guid>(), It.IsAny<FileByteRange?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task OpenFile_RefusalsAllSayNotFound()
    {
        PolicyWorld w = await PolicyWorld.CreateAsync();
        Guid policyId = await w.PublishAsync();

        Result<FileContent> notAssigned = await w.Policies().OpenVersionFileAsync(await w.CurrentVersionIdAsync(policyId), w.Bob.Id, w.StaffAt(w.Wisbech), null);
        Result<FileContent> noSuchVersion = await w.Policies().OpenVersionFileAsync(Guid.NewGuid(), w.Amy.Id, w.StaffAt(w.Ipswich), null);

        Assert.AreEqual(noSuchVersion.Error, notAssigned.Error);
    }
}
