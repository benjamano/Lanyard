using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Scheduling;

// A real RequiredTrainingService over a real CourseAssignmentService (email, branding and
// certificates mocked), so the assignment rows it writes can be checked. Shared with the
// position services' tests.
internal static class RequiredTrainingTestHelpers
{
    public static RequiredTrainingService Build(DbContextOptions<ApplicationDbContext> options, Mock<IEmailService>? email = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));

        email ??= new Mock<IEmailService>();
        email.Setup(e => e.SendTrainingAssignedEmailAsync(It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        Mock<ITrainingBrandingResolver> branding = new();
        branding.Setup(b => b.ResolveAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>())).ReturnsAsync(TrainingBranding.Default);

        CourseAssignmentService assignments = new(factory.Object, email.Object,
            Options.Create(new EmailOptions { PublicBaseUrl = "https://lanyard.example.com" }), branding.Object,
            new Mock<ICertificateService>().Object, NullLogger<CourseAssignmentService>.Instance);

        return new RequiredTrainingService(factory.Object, assignments, NullLogger<RequiredTrainingService>.Instance);
    }

    public static async Task<Course> CourseAsync(DbContextOptions<ApplicationDbContext> options, Company company, string name, int? locationId,
        bool isShared = false, bool isActive = true, int? recurrenceMonths = null)
    {
        await using ApplicationDbContext ctx = new(options);

        Course course = new() { Id = Guid.NewGuid(), CompanyId = company.Id, Name = name, LocationId = locationId, IsShared = isShared, IsActive = isActive, RecurrenceMonths = recurrenceMonths };
        ctx.Courses.Add(course);
        await ctx.SaveChangesAsync();

        return course;
    }

    public static async Task RequireAsync(DbContextOptions<ApplicationDbContext> options, StaffPosition position, Course course)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.StaffPositionRequiredCourses.Add(new StaffPositionRequiredCourse { Id = Guid.NewGuid(), StaffPositionId = position.Id, CourseId = course.Id, CreateDate = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    public static async Task GivePositionAsync(DbContextOptions<ApplicationDbContext> options, UserProfile user, StaffPosition position)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = user.Id, StaffPositionId = position.Id, IsPrimary = true, CreateDate = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    public static async Task AssignAsync(DbContextOptions<ApplicationDbContext> options, Course course, UserProfile user, DateTime? completedUtc)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.CourseAssignments.Add(new CourseAssignment
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            UserId = user.Id,
            AssignedDate = (completedUtc ?? DateTime.UtcNow).AddDays(-7),
            CompletedDate = completedUtc,
            IsActive = true
        });

        await ctx.SaveChangesAsync();
    }

    // Live assignments of this course for this person.
    public static async Task<List<CourseAssignment>> AssignmentsAsync(DbContextOptions<ApplicationDbContext> options, Course course, UserProfile user)
    {
        await using ApplicationDbContext ctx = new(options);

        return await ctx.CourseAssignments.Where(x => x.CourseId == course.Id && x.UserId == user.Id && x.IsActive).ToListAsync();
    }

    public static async Task<Location> AddLocationAsync(DbContextOptions<ApplicationDbContext> options, Company company, string name)
    {
        await using ApplicationDbContext ctx = new(options);

        Location location = new() { CompanyId = company.Id, Name = name, IsActive = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        return location;
    }
}

[TestClass]
public class RequiredTrainingServiceTests
{
    private sealed record World(DbContextOptions<ApplicationDbContext> Options, Company Company, Location Location, StaffPosition Marshal, UserProfile Amy, UserProfile Tom);

    private static async Task<World> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Play2Day", "Riverside");
        StaffPosition marshal = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Marshal");
        UserProfile amy = await SchedulingTestHelpers.SeedUserAsync(options, location, "Amy");
        UserProfile tom = await SchedulingTestHelpers.SeedUserAsync(options, location, "Tom");

        return new World(options, company, location, marshal, amy, tom);
    }

    private static Task<Result<RequiredTrainingAssignResult>> AssignAsync(World w, LocationScope scope, IReadOnlyCollection<Guid>? onlyCourseIds = null, Mock<IEmailService>? email = null) =>
        RequiredTrainingTestHelpers.Build(w.Options, email).AssignMissingAsync(scope, "manager",
            [(w.Amy.Id, w.Marshal.Id), (w.Tom.Id, w.Marshal.Id)], onlyCourseIds);

    [TestMethod]
    public async Task AssignMissing_AssignsTheCourseAndEmailsEachPerson()
    {
        World w = await SeedAsync();
        Course arena = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "Laser Arena Safety", w.Location.Id);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, arena);
        Mock<IEmailService> email = new();

        Result<RequiredTrainingAssignResult> result = await AssignAsync(w, SchedulingTestHelpers.AdminScope, email: email);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, result.Data!.Assigned);
        Assert.AreEqual("Assigned Laser Arena Safety to 2 people.", result.Data.Summary());
        Assert.AreEqual(1, (await RequiredTrainingTestHelpers.AssignmentsAsync(w.Options, arena, w.Amy)).Count);
        email.Verify(e => e.SendTrainingAssignedEmailAsync(It.IsAny<UserProfile>(), "Laser Arena Safety", null, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task AssignMissing_SkipsPeopleWhoHavePassedIt()
    {
        World w = await SeedAsync();
        Course arena = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "Laser Arena Safety", w.Location.Id);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, arena);
        await RequiredTrainingTestHelpers.AssignAsync(w.Options, arena, w.Amy, DateTime.UtcNow.AddDays(-30));

        Result<RequiredTrainingAssignResult> result = await AssignAsync(w, SchedulingTestHelpers.AdminScope);

        Assert.AreEqual(1, result.Data!.Assigned, "Only Tom");
        Assert.AreEqual(1, (await RequiredTrainingTestHelpers.AssignmentsAsync(w.Options, arena, w.Amy)).Count, "Amy's pass isn't followed by a new assignment");
    }

    [TestMethod]
    public async Task AssignMissing_SkipsPeopleAlreadyOnTheCourse()
    {
        World w = await SeedAsync();
        Course arena = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "Laser Arena Safety", w.Location.Id);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, arena);
        await RequiredTrainingTestHelpers.AssignAsync(w.Options, arena, w.Amy, completedUtc: null);

        Result<RequiredTrainingAssignResult> result = await AssignAsync(w, SchedulingTestHelpers.AdminScope);

        Assert.AreEqual(1, result.Data!.Assigned);
        Assert.AreEqual(1, result.Data.AlreadyOnIt);
        Assert.AreEqual(1, (await RequiredTrainingTestHelpers.AssignmentsAsync(w.Options, arena, w.Amy)).Count);
    }

    [TestMethod]
    public async Task AssignMissing_SaysSoWhenEveryoneIsAlreadyOnIt()
    {
        World w = await SeedAsync();
        Course arena = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "Laser Arena Safety", w.Location.Id);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, arena);
        await RequiredTrainingTestHelpers.AssignAsync(w.Options, arena, w.Amy, completedUtc: null);
        await RequiredTrainingTestHelpers.AssignAsync(w.Options, arena, w.Tom, DateTime.UtcNow.AddDays(-3));

        Result<RequiredTrainingAssignResult> result = await AssignAsync(w, SchedulingTestHelpers.AdminScope);

        Assert.AreEqual(0, result.Data!.Assigned);
        Assert.AreEqual("Nobody needed it assigning - they're already on it or have passed it.", result.Data.Summary());
    }

    [TestMethod]
    public async Task AssignMissing_ReassignsARecurringCourseThatHasRunOut()
    {
        World w = await SeedAsync();
        Course arena = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "Laser Arena Safety", w.Location.Id, recurrenceMonths: 12);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, arena);
        await RequiredTrainingTestHelpers.AssignAsync(w.Options, arena, w.Amy, DateTime.UtcNow.AddMonths(-13));
        await RequiredTrainingTestHelpers.AssignAsync(w.Options, arena, w.Tom, DateTime.UtcNow.AddMonths(-2));

        Result<RequiredTrainingAssignResult> result = await AssignAsync(w, SchedulingTestHelpers.AdminScope);

        Assert.AreEqual(1, result.Data!.Assigned, "Amy's has run out; Tom's is in date");
        Assert.AreEqual(2, (await RequiredTrainingTestHelpers.AssignmentsAsync(w.Options, arena, w.Amy)).Count);
    }

    [TestMethod]
    public async Task AssignMissing_OnlyTheCoursesAsked()
    {
        World w = await SeedAsync();
        Course arena = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "Laser Arena Safety", w.Location.Id);
        Course firstAid = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "First Aid", w.Location.Id);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, arena);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, firstAid);

        Result<RequiredTrainingAssignResult> result = await AssignAsync(w, SchedulingTestHelpers.AdminScope, onlyCourseIds: [firstAid.Id]);

        Assert.AreEqual("First Aid", result.Data!.Courses.Single().Name);
        Assert.AreEqual(0, (await RequiredTrainingTestHelpers.AssignmentsAsync(w.Options, arena, w.Amy)).Count);
    }

    [TestMethod]
    public async Task AssignMissing_AnotherLocationsOwnCourseIsntAssignedToStaffElsewhere()
    {
        World w = await SeedAsync();
        Location harbourside = await RequiredTrainingTestHelpers.AddLocationAsync(w.Options, w.Company, "Harbourside");
        UserProfile aisha = await SchedulingTestHelpers.SeedUserAsync(w.Options, harbourside, "Aisha");
        Course riversideOnly = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "Riverside Arena", w.Location.Id);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, riversideOnly);

        Result<RequiredTrainingAssignResult> result = await RequiredTrainingTestHelpers.Build(w.Options).AssignMissingAsync(
            SchedulingTestHelpers.AdminScope, "admin", [(w.Amy.Id, w.Marshal.Id), (aisha.Id, w.Marshal.Id)]);

        Assert.AreEqual(1, result.Data!.Assigned, "Amy only");
        Assert.AreEqual(0, (await RequiredTrainingTestHelpers.AssignmentsAsync(w.Options, riversideOnly, aisha)).Count);
    }

    [TestMethod]
    public async Task AssignMissing_NothingWhenTrainingIsSwitchedOff()
    {
        World w = await SeedAsync();
        Course arena = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "Laser Arena Safety", w.Location.Id);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, arena);

        await using (ApplicationDbContext ctx = new(w.Options))
        {
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting { CompanyId = w.Company.Id, Feature = CompanyFeature.Training, IsEnabled = false });
            await ctx.SaveChangesAsync();
        }

        Result<RequiredTrainingAssignResult> result = await AssignAsync(w, SchedulingTestHelpers.AdminScope);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, result.Data!.Assigned);
        Assert.IsNull(result.Data.Summary());
    }

    [TestMethod]
    public async Task AssignMissing_NothingForAnArchivedCourse()
    {
        World w = await SeedAsync();
        Course arena = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "Laser Arena Safety", w.Location.Id, isActive: false);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, arena);

        Result<RequiredTrainingAssignResult> result = await AssignAsync(w, SchedulingTestHelpers.AdminScope);

        Assert.AreEqual(0, result.Data!.Assigned);
    }

    [TestMethod]
    public async Task AssignMissing_ManagerCantAssignOutsideTheirScope()
    {
        World w = await SeedAsync();
        Location harbourside = await RequiredTrainingTestHelpers.AddLocationAsync(w.Options, w.Company, "Harbourside");
        UserProfile aisha = await SchedulingTestHelpers.SeedUserAsync(w.Options, harbourside, "Aisha");
        Course shared = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "Fire Safety", w.Location.Id, isShared: true);
        Course harbourOnly = await RequiredTrainingTestHelpers.CourseAsync(w.Options, w.Company, "Harbourside Arena", harbourside.Id);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, shared);
        await RequiredTrainingTestHelpers.RequireAsync(w.Options, w.Marshal, harbourOnly);

        // A Riverside manager: Aisha (Harbourside) can't be given the shared course from here, and
        // Harbourside's own course isn't theirs to hand out at all.
        Result<RequiredTrainingAssignResult> result = await RequiredTrainingTestHelpers.Build(w.Options).AssignMissingAsync(
            SchedulingTestHelpers.ManagerScopeFor(w.Location), "manager", [(w.Amy.Id, w.Marshal.Id), (aisha.Id, w.Marshal.Id)]);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.Data!.Assigned, "Amy gets Fire Safety");
        Assert.AreEqual(2, result.Data.CouldNotAssign, "Aisha's Fire Safety and Harbourside Arena");
        StringAssert.Contains(result.Data.Summary(), "couldn't be made from your location");
    }

    [TestMethod]
    public async Task AssignMissing_NothingRequiredIsANoOp()
    {
        World w = await SeedAsync();

        Result<RequiredTrainingAssignResult> result = await AssignAsync(w, SchedulingTestHelpers.AdminScope);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreSame(RequiredTrainingAssignResult.Empty, result.Data);
    }
}
