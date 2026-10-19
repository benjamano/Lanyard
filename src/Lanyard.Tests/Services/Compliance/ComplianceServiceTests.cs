using Lanyard.Application.Services.Compliance;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Compliance;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Compliance;

[TestClass]
public class ComplianceServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = Now.Date;

    private sealed class TestTenant(int? companyId, bool isSystem = false) : ITenantContext
    {
        public bool IsSystem => isSystem;
        public int? CompanyId => isSystem ? null : companyId;
        public bool IsPlatformAdmin => false;
        public int? ManageableCompanyId => isSystem ? null : companyId ?? -1;
        public bool CanManageCompany(int id) => ManageableCompanyId is null || ManageableCompanyId == id;
    }

    private record Fixture(
        DbContextOptions<ApplicationDbContext> Options,
        Company Company,
        Location Ipswich,
        Location Wisbech,
        Company OtherCompany,
        Location OtherLocation,
        UserProfile Amy,
        UserProfile Bob,
        UserProfile Cara,
        Course Course,
        StaffDocumentType DbsCheck);

    private static async Task<Fixture> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Play2Day", "Ipswich");
        (Company otherCompany, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Location wisbech;
        Course course;
        StaffDocumentType dbsCheck;

        await using (ApplicationDbContext ctx = new(options))
        {
            wisbech = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            course = new Course { CompanyId = company.Id, Name = "Fire Safety", IsActive = true, LocationId = ipswich.Id };
            dbsCheck = new StaffDocumentType { CompanyId = company.Id, Name = "DBS check", RequiresExpiryDate = true, IsActive = true };

            ctx.Locations.Add(wisbech);
            ctx.Courses.Add(course);
            ctx.StaffDocumentTypes.Add(dbsCheck);
            await ctx.SaveChangesAsync();
        }

        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy");
        UserProfile bob = await SchedulingTestHelpers.SeedUserAsync(options, wisbech, "Bob");
        UserProfile cara = await SchedulingTestHelpers.SeedUserAsync(options, otherLocation, "Cara");

        return new Fixture(options, company, ipswich, wisbech, otherCompany, otherLocation, amy, bob, cara, course, dbsCheck);
    }

    private static ComplianceService ServiceFor(Fixture f, ITenantContext? tenant = null) =>
        new(SchedulingTestHelpers.GetFactory(f.Options), tenant ?? new TestTenant(f.Company.Id), new TestClock(Now), NullLogger<ComplianceService>.Instance);

    private static LocationScope AdminScope(Fixture f) => new(true, f.Ipswich.Id, f.Company.Id, "Play2Day Ipswich");

    private static async Task AddAssignmentAsync(Fixture f, UserProfile user, DateTime? dueDate, int? locationId,
        DateTime? completedDate = null, bool isActive = true, Guid? courseId = null)
    {
        await using ApplicationDbContext ctx = new(f.Options);
        ctx.CourseAssignments.Add(new CourseAssignment
        {
            CourseId = courseId ?? f.Course.Id,
            UserId = user.Id,
            AssignedDate = Now.AddDays(-60),
            DueDate = dueDate,
            CompletedDate = completedDate,
            IsActive = isActive,
            LocationId = locationId
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task AddDocumentAsync(Fixture f, UserProfile user, DateTime? expiryDate, DateTime uploadedDate,
        Guid? typeId = null, int? companyId = null, bool isActive = true)
    {
        await using ApplicationDbContext ctx = new(f.Options);
        ctx.StaffDocuments.Add(new StaffDocument
        {
            CompanyId = companyId ?? f.Company.Id,
            UserId = user.Id,
            StaffDocumentTypeId = typeId ?? f.DbsCheck.Id,
            FileMetadataId = Guid.NewGuid(),
            ExpiryDate = expiryDate,
            UploadedDate = uploadedDate,
            UploadedByUserId = ApplicationDbContext.SeedAdminUserId,
            IsActive = isActive
        });
        await ctx.SaveChangesAsync();
    }

    // ---- Refusals ----

    [TestMethod]
    public async Task GetSummary_FailsForASystemCaller()
    {
        Fixture f = await SeedAsync();
        await AddDocumentAsync(f, f.Amy, Today.AddDays(-5), Now.AddDays(-400));
        ComplianceService service = ServiceFor(f, new TestTenant(null, isSystem: true));

        Result<ComplianceSummary> result = await service.GetSummaryAsync(AdminScope(f), new ComplianceQuery(null, 30));

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task GetSummary_FailsForAPlainStaffMember()
    {
        Fixture f = await SeedAsync();
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(SchedulingTestHelpers.StaffScopeFor(f.Ipswich), new ComplianceQuery(null, 30));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetSummary_FailsForAManagerWithNoLocation()
    {
        Fixture f = await SeedAsync();
        ComplianceService service = ServiceFor(f);
        LocationScope managerWithoutLocation = new(false, null, f.Company.Id, null, IsManager: true);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(managerWithoutLocation, new ComplianceQuery(null, 30));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetSummary_FailsWhenTheCompanyCannotBeWorkedOut()
    {
        Fixture f = await SeedAsync();
        ComplianceService service = ServiceFor(f, new TestTenant(null));

        Result<ComplianceSummary> result = await service.GetSummaryAsync(AdminScope(f), new ComplianceQuery(null, 30));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(366)]
    [DataRow(-1)]
    public async Task GetSummary_FailsForAnExpiryWindowOutOfRange(int days)
    {
        Fixture f = await SeedAsync();
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(AdminScope(f), new ComplianceQuery(null, days));

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "between 1 and 365");
    }

    [TestMethod]
    public async Task GetSummary_FailsForANegativeRowLimit()
    {
        Fixture f = await SeedAsync();
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(AdminScope(f), new ComplianceQuery(null, 30, -1));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetSummary_FailsWhenAnAdminAsksForAnotherCompanysLocation()
    {
        Fixture f = await SeedAsync();
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(AdminScope(f), new ComplianceQuery(f.OtherLocation.Id, 30));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("That location isn't available.", result.Error);
    }

    [TestMethod]
    public async Task GetSummary_ReturnsFailRatherThanThrowingWhenTheDatabaseFails()
    {
        Fixture f = await SeedAsync();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database is down"));
        ComplianceService service = new(factory.Object, new TestTenant(f.Company.Id), new TestClock(Now), NullLogger<ComplianceService>.Instance);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(AdminScope(f), new ComplianceQuery(null, 30));

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "database is down");
    }

    // ---- Overdue training ----

    [TestMethod]
    public async Task OverdueTraining_CountsOnlyActiveIncompleteAssignmentsPastTheirDueDate()
    {
        Fixture f = await SeedAsync();
        await AddAssignmentAsync(f, f.Amy, Now.AddDays(-3), f.Ipswich.Id);                                  // counts
        await AddAssignmentAsync(f, f.Amy, Now.AddDays(3), f.Ipswich.Id);                                   // not due yet
        await AddAssignmentAsync(f, f.Amy, Now.AddDays(-3), f.Ipswich.Id, completedDate: Now.AddDays(-4));  // completed
        await AddAssignmentAsync(f, f.Amy, Now.AddDays(-3), f.Ipswich.Id, isActive: false);                 // removed
        await AddAssignmentAsync(f, f.Amy, null, f.Ipswich.Id);                                             // no due date

        Guid retiredCourseId;
        await using (ApplicationDbContext ctx = new(f.Options))
        {
            Course retired = new() { CompanyId = f.Company.Id, Name = "Retired", IsActive = false, LocationId = f.Ipswich.Id };
            ctx.Courses.Add(retired);
            await ctx.SaveChangesAsync();
            retiredCourseId = retired.Id;
        }
        await AddAssignmentAsync(f, f.Amy, Now.AddDays(-3), f.Ipswich.Id, courseId: retiredCourseId);       // inactive course

        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(null, 30));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.Data!.OverdueTraining.ItemCount);
        Assert.AreEqual(1, result.Data.OverdueTraining.PeopleCount);
        ComplianceRow row = result.Data.OverdueTraining.Rows.Single();
        Assert.AreEqual(f.Amy.Id, row.UserId);
        Assert.AreEqual("Amy Tester", row.DisplayName);
        Assert.AreEqual("Fire Safety · 3 days overdue", row.Detail);
    }

    [TestMethod]
    public async Task Manager_IsPinnedToTheirOwnLocationWhateverIsAskedFor()
    {
        Fixture f = await SeedAsync();
        await AddAssignmentAsync(f, f.Amy, Now.AddDays(-1), f.Ipswich.Id);
        await AddAssignmentAsync(f, f.Bob, Now.AddDays(-1), f.Wisbech.Id);
        await AddDocumentAsync(f, f.Bob, Today.AddDays(-2), Now.AddDays(-300));
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(f.Wisbech.Id, 30));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(f.Ipswich.Id, result.Data!.LocationId);
        CollectionAssert.AreEqual(new[] { f.Amy.Id }, result.Data.OverdueTraining.Rows.Select(x => x.UserId).ToArray());
        Assert.AreEqual(0, result.Data.ExpiredDocuments.ItemCount);
    }

    [TestMethod]
    public async Task Admin_AllLocations_IncludesEveryLocationAndUnplacedAssignments()
    {
        Fixture f = await SeedAsync();
        await AddAssignmentAsync(f, f.Amy, Now.AddDays(-1), f.Ipswich.Id);
        await AddAssignmentAsync(f, f.Bob, Now.AddDays(-1), f.Wisbech.Id);
        await AddAssignmentAsync(f, f.Bob, Now.AddDays(-2), null);
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(AdminScope(f), new ComplianceQuery(null, 30));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("All locations", result.Data!.LocationName);
        Assert.AreEqual(3, result.Data.OverdueTraining.ItemCount);
        Assert.AreEqual(2, result.Data.OverdueTraining.PeopleCount);
    }

    [TestMethod]
    public async Task Admin_OneLocation_LeavesOutOtherLocationsAndUnplacedAssignments()
    {
        Fixture f = await SeedAsync();
        await AddAssignmentAsync(f, f.Amy, Now.AddDays(-1), f.Ipswich.Id);
        await AddAssignmentAsync(f, f.Bob, Now.AddDays(-1), f.Wisbech.Id);
        await AddAssignmentAsync(f, f.Bob, Now.AddDays(-2), null);
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(AdminScope(f), new ComplianceQuery(f.Wisbech.Id, 30));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Play2Day Wisbech", result.Data!.LocationName);
        Assert.AreEqual(1, result.Data.OverdueTraining.ItemCount);
        Assert.AreEqual(f.Bob.Id, result.Data.OverdueTraining.Rows.Single().UserId);
    }

    // ---- Staff documents ----

    [TestMethod]
    public async Task Documents_ARenewedDocumentIsNotCountedAsExpired()
    {
        Fixture f = await SeedAsync();
        await AddDocumentAsync(f, f.Amy, Today.AddDays(-10), Now.AddDays(-400)); // the old one, still active
        await AddDocumentAsync(f, f.Amy, Today.AddDays(355), Now.AddDays(-9));   // the renewal
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(null, 30));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, result.Data!.ExpiredDocuments.ItemCount);
        Assert.AreEqual(0, result.Data.ExpiringDocuments.ItemCount);
    }

    [TestMethod]
    public async Task Documents_ARenewalWithNoExpiryClearsTheOldExpiredOne()
    {
        Fixture f = await SeedAsync();
        await AddDocumentAsync(f, f.Amy, Today.AddDays(-10), Now.AddDays(-400));
        await AddDocumentAsync(f, f.Amy, null, Now.AddDays(-1));
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(null, 30));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, result.Data!.ExpiredDocuments.ItemCount);
    }

    [TestMethod]
    public async Task Documents_SplitIntoExpiredAndExpiringAtTheWindowEdges()
    {
        Fixture f = await SeedAsync();
        StaffDocumentType rightToWork = await AddTypeAsync(f, "Right to work");
        StaffDocumentType firstAid = await AddTypeAsync(f, "First aid");
        StaffDocumentType foodHygiene = await AddTypeAsync(f, "Food hygiene");

        await AddDocumentAsync(f, f.Amy, Today.AddDays(-1), Now.AddDays(-300));                    // expired yesterday
        await AddDocumentAsync(f, f.Amy, Today, Now.AddDays(-300), rightToWork.Id);                // expires today: expiring
        await AddDocumentAsync(f, f.Amy, Today.AddDays(30), Now.AddDays(-300), firstAid.Id);       // last day of the window
        await AddDocumentAsync(f, f.Amy, Today.AddDays(31), Now.AddDays(-300), foodHygiene.Id);    // just outside
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(null, 30));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("DBS check · expired yesterday", result.Data!.ExpiredDocuments.Rows.Single().Detail);
        CollectionAssert.AreEqual(
            new[] { "Right to work · expires today", "First aid · expires in 30 days" },
            result.Data.ExpiringDocuments.Rows.Select(x => x.Detail).ToArray());
        Assert.AreEqual(1, result.Data.ExpiringDocuments.PeopleCount);
    }

    [TestMethod]
    public async Task Documents_IgnoreInactiveDocumentsInactiveTypesAndTheDeletedUserPlaceholder()
    {
        Fixture f = await SeedAsync();
        StaffDocumentType retired = await AddTypeAsync(f, "Retired", isActive: false);

        UserProfile placeholder;
        await using (ApplicationDbContext ctx = new(f.Options))
        {
            placeholder = new UserProfile { Id = ApplicationDbContext.SystemDeletedUserPlaceholderId, UserName = "deleted" };
            ctx.Users.Add(placeholder);
            ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = placeholder.Id, LocationId = f.Ipswich.Id });
            await ctx.SaveChangesAsync();
        }

        await AddDocumentAsync(f, f.Amy, Today.AddDays(-5), Now.AddDays(-300), isActive: false);
        await AddDocumentAsync(f, f.Amy, Today.AddDays(-5), Now.AddDays(-300), retired.Id);
        await AddDocumentAsync(f, placeholder, Today.AddDays(-5), Now.AddDays(-300));
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(null, 30));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, result.Data!.ExpiredDocuments.ItemCount);
    }

    [TestMethod]
    public async Task Documents_StaffAtAnotherCompanyAreNeverCounted()
    {
        Fixture f = await SeedAsync();
        await AddDocumentAsync(f, f.Cara, Today.AddDays(-5), Now.AddDays(-300), companyId: f.OtherCompany.Id);
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(AdminScope(f), new ComplianceQuery(null, 30));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, result.Data!.ExpiredDocuments.ItemCount);
        Assert.IsFalse(result.Data.Sections.SelectMany(x => x.Rows).Any(x => x.UserId == f.Cara.Id));
    }

    [TestMethod]
    public async Task MaxRows_CapsTheNamesButNotTheCounts()
    {
        Fixture f = await SeedAsync();
        UserProfile dan = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Ipswich, "Dan");
        UserProfile eve = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Ipswich, "Eve");

        await AddDocumentAsync(f, f.Amy, Today.AddDays(-3), Now.AddDays(-300));
        await AddDocumentAsync(f, dan, Today.AddDays(-2), Now.AddDays(-300));
        await AddDocumentAsync(f, eve, Today.AddDays(-1), Now.AddDays(-300));
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> result = await service.GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(null, 30, 2));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(3, result.Data!.ExpiredDocuments.ItemCount);
        Assert.AreEqual(3, result.Data.ExpiredDocuments.PeopleCount);
        // Longest-expired first.
        CollectionAssert.AreEqual(new[] { f.Amy.Id, dan.Id }, result.Data.ExpiredDocuments.Rows.Select(x => x.UserId).ToArray());
    }

    // ---- Policies ----

    private static async Task<Policy> AddPolicyAsync(Fixture f, string name, int? locationId, bool isActive = true, int? companyId = null)
    {
        await using ApplicationDbContext ctx = new(f.Options);
        Policy policy = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId ?? f.Company.Id,
            Name = name,
            LocationId = locationId,
            CurrentVersionNumber = 1,
            RequiredVersionNumber = 1,
            IsActive = isActive
        };
        ctx.Policies.Add(policy);
        await ctx.SaveChangesAsync();
        return policy;
    }

    // outstandingFrom null = up to date.
    private static async Task AddPolicyAssignmentAsync(Policy policy, Fixture f, UserProfile user, DateTime? outstandingFrom, DateTime? dueDate, bool isActive = true)
    {
        await using ApplicationDbContext ctx = new(f.Options);
        ctx.PolicyAssignments.Add(new PolicyAssignment
        {
            Id = Guid.NewGuid(),
            CompanyId = policy.CompanyId,
            PolicyId = policy.Id,
            UserId = user.Id,
            AssignedDate = Now.AddDays(-30),
            OutstandingFrom = outstandingFrom,
            DueDate = dueDate,
            IsActive = isActive
        });
        await ctx.SaveChangesAsync();
    }

    [TestMethod]
    public async Task Policies_SplitIntoOverdueAndToRead()
    {
        Fixture f = await SeedAsync();
        UserProfile dan = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Ipswich, "Dan");
        Policy fire = await AddPolicyAsync(f, "Fire policy", f.Ipswich.Id);
        await AddPolicyAssignmentAsync(fire, f, f.Amy, Now.AddDays(-20), Now.AddDays(-3));
        await AddPolicyAssignmentAsync(fire, f, dan, Now.AddDays(-2), Now.AddDays(5));

        Result<ComplianceSummary> result = await ServiceFor(f).GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(null, 30));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Fire policy · 3 days overdue", result.Data!.OverduePolicies.Rows.Single().Detail);
        Assert.AreEqual(f.Amy.Id, result.Data.OverduePolicies.Rows.Single().UserId);
        Assert.AreEqual("Fire policy · due in 5 days", result.Data.OutstandingPolicies.Rows.Single().Detail);
        Assert.AreEqual(dan.Id, result.Data.OutstandingPolicies.Rows.Single().UserId);
    }

    [TestMethod]
    public async Task Policies_IgnoreUpToDate_NotYetOpen_Inactive_AndRemovedPolicies()
    {
        Fixture f = await SeedAsync();
        UserProfile dan = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Ipswich, "Dan");
        UserProfile eve = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Ipswich, "Eve");
        Policy fire = await AddPolicyAsync(f, "Fire policy", f.Ipswich.Id);
        Policy removed = await AddPolicyAsync(f, "Old policy", f.Ipswich.Id, isActive: false);

        await AddPolicyAssignmentAsync(fire, f, f.Amy, null, null);                                  // up to date
        await AddPolicyAssignmentAsync(fire, f, dan, Now.AddDays(10), Now.AddDays(24));              // re-read window not open yet
        await AddPolicyAssignmentAsync(fire, f, eve, Now.AddDays(-20), Now.AddDays(-3), isActive: false); // left the audience
        await AddPolicyAssignmentAsync(removed, f, f.Amy, Now.AddDays(-20), Now.AddDays(-3));

        Result<ComplianceSummary> result = await ServiceFor(f).GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(null, 30));

        Assert.AreEqual(0, result.Data!.OverduePolicies.ItemCount);
        Assert.AreEqual(0, result.Data.OutstandingPolicies.ItemCount);
    }

    [TestMethod]
    public async Task Policies_OneLocation_ShowsItsOwnAndCompanyWide_AdminWholeCompanyShowsAll()
    {
        Fixture f = await SeedAsync();
        Policy ipswich = await AddPolicyAsync(f, "Ipswich policy", f.Ipswich.Id);
        Policy wisbech = await AddPolicyAsync(f, "Wisbech policy", f.Wisbech.Id);
        Policy handbook = await AddPolicyAsync(f, "Handbook", null);
        Policy other = await AddPolicyAsync(f, "Other company policy", f.OtherLocation.Id, companyId: f.OtherCompany.Id);

        await AddPolicyAssignmentAsync(ipswich, f, f.Amy, Now.AddDays(-1), Now.AddDays(10));
        await AddPolicyAssignmentAsync(handbook, f, f.Amy, Now.AddDays(-1), Now.AddDays(10));
        await AddPolicyAssignmentAsync(wisbech, f, f.Bob, Now.AddDays(-1), Now.AddDays(10));
        await AddPolicyAssignmentAsync(handbook, f, f.Bob, Now.AddDays(-1), Now.AddDays(10));
        await AddPolicyAssignmentAsync(other, f, f.Cara, Now.AddDays(-1), Now.AddDays(10));
        ComplianceService service = ServiceFor(f);

        Result<ComplianceSummary> ipswichView = await service.GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(null, 30));
        Result<ComplianceSummary> wholeCompany = await service.GetSummaryAsync(AdminScope(f), new ComplianceQuery(null, 30));

        CollectionAssert.AreEquivalent(new[] { "Ipswich policy · due in 10 days", "Handbook · due in 10 days" },
            ipswichView.Data!.OutstandingPolicies.Rows.Select(x => x.Detail).ToArray());
        Assert.AreEqual(4, wholeCompany.Data!.OutstandingPolicies.ItemCount);
        Assert.AreEqual(2, wholeCompany.Data.OutstandingPolicies.PeopleCount);
    }

    [TestMethod]
    public async Task Policies_FeatureSwitchedOff_SectionsAreEmpty()
    {
        Fixture f = await SeedAsync();
        Policy fire = await AddPolicyAsync(f, "Fire policy", f.Ipswich.Id);
        await AddPolicyAssignmentAsync(fire, f, f.Amy, Now.AddDays(-20), Now.AddDays(-3));

        await using (ApplicationDbContext ctx = new(f.Options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = f.Company.Id, Feature = Infrastructure.Enum.CompanyFeature.Policies, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        Result<ComplianceSummary> result = await ServiceFor(f).GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(null, 30));

        Assert.AreEqual(0, result.Data!.OverduePolicies.ItemCount);
    }

    [TestMethod]
    public async Task Policies_MaxRows_CapsTheNamesButNotTheCounts()
    {
        Fixture f = await SeedAsync();
        UserProfile dan = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Ipswich, "Dan");
        UserProfile eve = await SchedulingTestHelpers.SeedUserAsync(f.Options, f.Ipswich, "Eve");
        Policy fire = await AddPolicyAsync(f, "Fire policy", f.Ipswich.Id);
        await AddPolicyAssignmentAsync(fire, f, f.Amy, Now.AddDays(-30), Now.AddDays(-9));
        await AddPolicyAssignmentAsync(fire, f, dan, Now.AddDays(-30), Now.AddDays(-5));
        await AddPolicyAssignmentAsync(fire, f, eve, Now.AddDays(-30), Now.AddDays(-1));

        Result<ComplianceSummary> result = await ServiceFor(f).GetSummaryAsync(SchedulingTestHelpers.ManagerScopeFor(f.Ipswich), new ComplianceQuery(null, 30, 2));

        Assert.AreEqual(3, result.Data!.OverduePolicies.ItemCount);
        CollectionAssert.AreEqual(new[] { f.Amy.Id, dan.Id }, result.Data.OverduePolicies.Rows.Select(x => x.UserId).ToArray());
    }

    private static async Task<StaffDocumentType> AddTypeAsync(Fixture f, string name, bool isActive = true)
    {
        await using ApplicationDbContext ctx = new(f.Options);
        StaffDocumentType type = new() { CompanyId = f.Company.Id, Name = name, RequiresExpiryDate = true, IsActive = isActive };
        ctx.StaffDocumentTypes.Add(type);
        await ctx.SaveChangesAsync();
        return type;
    }
}
