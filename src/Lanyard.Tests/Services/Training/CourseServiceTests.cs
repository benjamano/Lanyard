using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Training;

[TestClass]
public class CourseServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static CourseService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new CourseService(factoryMock.Object);
    }

    private static async Task<Course> SeedCourseAsync(DbContextOptions<ApplicationDbContext> options, string name = "Play2Day Induction")
    {
        await using ApplicationDbContext ctx = new(options);

        Course course = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            PassMarkPercent = 80,
            IsActive = true
        };

        ctx.Courses.Add(course);
        await ctx.SaveChangesAsync();

        return course;
    }

    private static readonly LocationScope AdminScope = new(true, null, null, null);

    private static async Task<(Location locationA, Location locationB)> SeedTwoLocationsAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = "Play2Day", IsActive = true };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        Location ipswich = new() { CompanyId = company.Id, Name = "Ipswich", IsActive = true };
        Location wisbech = new() { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
        ctx.Locations.AddRange(ipswich, wisbech);
        await ctx.SaveChangesAsync();

        return (ipswich, wisbech);
    }

    [TestMethod]
    public async Task CourseService_GetCourses_NonAdminSeesOwnLocationAndSharedCoursesOnly()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = Guid.NewGuid(), Name = "Ipswich Only", PassMarkPercent = 80, IsActive = true, LocationId = ipswich.Id });
            ctx.Courses.Add(new Course { Id = Guid.NewGuid(), Name = "Wisbech Only", PassMarkPercent = 80, IsActive = true, LocationId = wisbech.Id });
            ctx.Courses.Add(new Course { Id = Guid.NewGuid(), Name = "COSHH Shared", PassMarkPercent = 80, IsActive = true, LocationId = wisbech.Id, IsShared = true });
            await ctx.SaveChangesAsync();
        }

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<List<Course>> result = await service.GetCoursesAsync(ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(2, result.Data!);
        CollectionAssert.AreEquivalent(new[] { "COSHH Shared", "Ipswich Only" }, result.Data!.Select(x => x.Name).ToList());
    }

    [TestMethod]
    public async Task CourseService_GetCourses_AdminSeesEveryLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = Guid.NewGuid(), Name = "Ipswich Only", PassMarkPercent = 80, IsActive = true, LocationId = ipswich.Id });
            ctx.Courses.Add(new Course { Id = Guid.NewGuid(), Name = "Wisbech Only", PassMarkPercent = 80, IsActive = true, LocationId = wisbech.Id });
            await ctx.SaveChangesAsync();
        }

        Result<List<Course>> result = await service.GetCoursesAsync(AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(2, result.Data!);
    }

    [TestMethod]
    public async Task CourseService_SaveCourse_NonAdminCreate_ForcesScopeLocationIgnoringClientValue()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);
        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");

        Course tampered = new() { Id = Guid.Empty, Name = "New Course", PassMarkPercent = 80, LocationId = wisbech.Id };

        Result<Course> result = await service.SaveCourseAsync(tampered, ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(ipswich.Id, result.Data!.LocationId);
    }

    [TestMethod]
    public async Task CourseService_SaveCourse_NonAdminEditingAnotherLocationsCourse_Fails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);

        Guid courseId = Guid.NewGuid();
        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = courseId, Name = "Wisbech Course", PassMarkPercent = 80, IsActive = true, LocationId = wisbech.Id });
            await ctx.SaveChangesAsync();
        }

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<Course> result = await service.SaveCourseAsync(
            new Course { Id = courseId, Name = "Renamed", PassMarkPercent = 80 }, ipswichScope);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("You do not have access to this course.", result.Error);
    }

    [TestMethod]
    public async Task CourseService_GetCourse_OutOfScopeCourse_ReturnsNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);

        Guid courseId = Guid.NewGuid();
        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = courseId, Name = "Wisbech Course", PassMarkPercent = 80, IsActive = true, LocationId = wisbech.Id });
            await ctx.SaveChangesAsync();
        }

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<Course> result = await service.GetCourseAsync(courseId, ipswichScope);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Course not found.", result.Error);
    }

    [TestMethod]
    public async Task CourseService_GetCourse_SharedCourseWithNoLocation_ReturnsNotFoundForNonAdminInsteadOfThrowing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);

        // An Admin can currently save a course with IsShared set and no LocationId
        // (SaveCourseAsync only forces a location for non-Admins on create). This
        // seeds that exact combination directly, bypassing the service.
        Guid courseId = Guid.NewGuid();
        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = courseId, Name = "Shared No Location", PassMarkPercent = 80, IsActive = true, LocationId = null, IsShared = true });
            await ctx.SaveChangesAsync();
        }

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<Course> result = await service.GetCourseAsync(courseId, ipswichScope);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Course not found.", result.Error);
    }

    [TestMethod]
    public async Task CourseService_GetCourses_ReturnsOnlyActiveCoursesOrderedByName()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);

        await SeedCourseAsync(options, "Zebra Course");
        await SeedCourseAsync(options, "Alpha Course");

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = Guid.NewGuid(), Name = "Inactive Course", PassMarkPercent = 80, IsActive = false });
            await ctx.SaveChangesAsync();
        }

        Result<List<Course>> result = await service.GetCoursesAsync(AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(2, result.Data!);
        Assert.AreEqual("Alpha Course", result.Data![0].Name);
        Assert.AreEqual("Zebra Course", result.Data![1].Name);
    }

    [TestMethod]
    public async Task CourseService_SaveCourse_CreatesNewCourseWithDefaults()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);

        Course newCourse = new() { Id = Guid.Empty, Name = "New Course", PassMarkPercent = 80 };

        Result<Course> result = await service.SaveCourseAsync(newCourse, AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreNotEqual(Guid.Empty, result.Data!.Id);

        await using ApplicationDbContext ctx = new(options);
        Course dbCourse = await ctx.Courses.SingleAsync(x => x.Id == result.Data.Id);
        Assert.AreEqual("New Course", dbCourse.Name);
        Assert.IsTrue(dbCourse.IsActive);
    }

    [TestMethod]
    public async Task CourseService_SaveCourse_UpdatesExistingCourseFields()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        course.Name = "Renamed Course";
        course.PassMarkPercent = 90;
        course.AutoAssignOnUserCreation = true;

        Result<Course> result = await service.SaveCourseAsync(course, AdminScope);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Course dbCourse = await ctx.Courses.SingleAsync(x => x.Id == course.Id);
        Assert.AreEqual("Renamed Course", dbCourse.Name);
        Assert.AreEqual(90, dbCourse.PassMarkPercent);
        Assert.IsTrue(dbCourse.AutoAssignOnUserCreation);
    }

    [TestMethod]
    public async Task CourseService_SaveCourse_FailsWhenNameMissing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);

        Result<Course> result = await service.SaveCourseAsync(new Course { Id = Guid.Empty, Name = "   ", PassMarkPercent = 80 }, AdminScope);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CourseService_DeleteCourse_SetsInactive()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        Result<bool> result = await service.DeleteCourseAsync(course.Id, AdminScope);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Course dbCourse = await ctx.Courses.SingleAsync(x => x.Id == course.Id);
        Assert.IsFalse(dbCourse.IsActive);
    }

    [TestMethod]
    public async Task CourseService_GetCourse_ReturnsFailWhenNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);

        Result<Course> result = await service.GetCourseAsync(Guid.NewGuid(), AdminScope);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CourseService_SaveSection_CreatesSectionWithNextSortOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        Result<CourseSection> first = await service.SaveSectionAsync(new CourseSection { Id = Guid.Empty, CourseId = course.Id, Title = "Shoes" });
        Result<CourseSection> second = await service.SaveSectionAsync(new CourseSection { Id = Guid.Empty, CourseId = course.Id, Title = "Jewellery" });

        Assert.IsTrue(first.Success, first.Error);
        Assert.IsTrue(second.Success, second.Error);
        Assert.AreEqual(0, first.Data!.SortOrder);
        Assert.AreEqual(1, second.Data!.SortOrder);
    }

    [TestMethod]
    public async Task CourseService_SaveSection_UpdatesExistingSectionTitleAndBody()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        Result<CourseSection> created = await service.SaveSectionAsync(new CourseSection { Id = Guid.Empty, CourseId = course.Id, Title = "Shoes" });
        Assert.IsTrue(created.Success, created.Error);

        created.Data!.Title = "Footwear";
        created.Data!.BodyHtml = "<p>Closed toe only.</p>";

        Result<CourseSection> updated = await service.SaveSectionAsync(created.Data!);

        Assert.IsTrue(updated.Success, updated.Error);
        Assert.AreEqual("Footwear", updated.Data!.Title);
        Assert.AreEqual("<p>Closed toe only.</p>", updated.Data!.BodyHtml);
    }

    [TestMethod]
    public async Task CourseService_SaveSection_FailsWhenTitleMissing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        Result<CourseSection> result = await service.SaveSectionAsync(new CourseSection { Id = Guid.Empty, CourseId = course.Id, Title = " " });

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CourseService_DeleteSection_SetsInactiveAndExcludesFromGetCourse()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        Result<CourseSection> created = await service.SaveSectionAsync(new CourseSection { Id = Guid.Empty, CourseId = course.Id, Title = "Shoes" });

        Result<bool> deleteResult = await service.DeleteSectionAsync(created.Data!.Id);
        Assert.IsTrue(deleteResult.Success, deleteResult.Error);

        Result<Course> reloaded = await service.GetCourseAsync(course.Id, AdminScope);
        Assert.IsTrue(reloaded.Success, reloaded.Error);
        Assert.HasCount(0, reloaded.Data!.Sections);
    }

    private static CourseQuestion BuildQuestion(Guid courseId, params (string text, bool isCorrect)[] options)
    {
        return new CourseQuestion
        {
            Id = Guid.Empty,
            CourseId = courseId,
            QuestionText = "If I am ill I should...",
            Options = [.. options.Select(o => new CourseQuestionOption { Id = Guid.Empty, QuestionId = Guid.Empty, OptionText = o.text, IsCorrect = o.isCorrect })]
        };
    }

    [TestMethod]
    public async Task CourseService_SaveQuestion_CreatesQuestionWithOptions()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        CourseQuestion question = BuildQuestion(course.Id,
            ("Ring Play2Day and tell a manager.", true),
            ("Say nothing.", false));

        Result<CourseQuestion> result = await service.SaveQuestionAsync(question);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(2, result.Data!.Options);
        Assert.AreEqual("Ring Play2Day and tell a manager.", result.Data!.Options.Single(x => x.IsCorrect).OptionText);
    }

    [TestMethod]
    public async Task CourseService_SaveQuestion_FailsWhenFewerThanTwoActiveOptions()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        CourseQuestion question = BuildQuestion(course.Id, ("Only option.", true));

        Result<CourseQuestion> result = await service.SaveQuestionAsync(question);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CourseService_SaveQuestion_FailsWhenNoOptionMarkedCorrect()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        CourseQuestion question = BuildQuestion(course.Id, ("A", false), ("B", false));

        Result<CourseQuestion> result = await service.SaveQuestionAsync(question);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CourseService_SaveQuestion_FailsWhenMultipleOptionsMarkedCorrect()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        CourseQuestion question = BuildQuestion(course.Id, ("A", true), ("B", true));

        Result<CourseQuestion> result = await service.SaveQuestionAsync(question);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CourseService_SaveQuestion_UpdatesOptionsAndSoftDeletesRemoved()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        Result<CourseQuestion> created = await service.SaveQuestionAsync(BuildQuestion(course.Id, ("A", true), ("B", false)));
        Assert.IsTrue(created.Success, created.Error);

        CourseQuestionOption keep = created.Data!.Options.Single(x => x.OptionText == "A");

        CourseQuestion update = new()
        {
            Id = created.Data!.Id,
            CourseId = course.Id,
            QuestionText = "Updated question text",
            Options =
            [
                new CourseQuestionOption { Id = keep.Id, QuestionId = created.Data!.Id, OptionText = "A", IsCorrect = true },
                new CourseQuestionOption { Id = Guid.NewGuid(), QuestionId = created.Data!.Id, OptionText = "C", IsCorrect = false }
            ]
        };

        Result<CourseQuestion> updated = await service.SaveQuestionAsync(update);

        Assert.IsTrue(updated.Success, updated.Error);
        Assert.HasCount(2, updated.Data!.Options);
        Assert.IsTrue(updated.Data!.Options.Any(x => x.OptionText == "C"));
        Assert.IsFalse(updated.Data!.Options.Any(x => x.OptionText == "B"));
    }

    [TestMethod]
    public async Task CourseService_SaveQuestion_ReturnsAndReloadsOptionsOrderedBySortOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        // Options are supplied out of SortOrder order to prove the ordering is driven by
        // SortOrder (as CourseEditor.razor now assigns by list index) rather than by
        // insertion/list order, which is what silently broke before this fix.
        CourseQuestion question = new()
        {
            Id = Guid.Empty,
            CourseId = course.Id,
            QuestionText = "Order test",
            Options =
            [
                new CourseQuestionOption { Id = Guid.Empty, QuestionId = Guid.Empty, OptionText = "Second", IsCorrect = false, SortOrder = 1 },
                new CourseQuestionOption { Id = Guid.Empty, QuestionId = Guid.Empty, OptionText = "First", IsCorrect = true, SortOrder = 0 }
            ]
        };

        Result<CourseQuestion> saved = await service.SaveQuestionAsync(question);

        Assert.IsTrue(saved.Success, saved.Error);
        Assert.HasCount(2, saved.Data!.Options);
        Assert.AreEqual("First", saved.Data!.Options[0].OptionText);
        Assert.AreEqual("Second", saved.Data!.Options[1].OptionText);

        Result<Course> reloaded = await service.GetCourseAsync(course.Id, AdminScope);

        Assert.IsTrue(reloaded.Success, reloaded.Error);
        CourseQuestion reloadedQuestion = reloaded.Data!.Questions.Single(x => x.Id == saved.Data!.Id);
        Assert.AreEqual("First", reloadedQuestion.Options[0].OptionText);
        Assert.AreEqual("Second", reloadedQuestion.Options[1].OptionText);
    }

    [TestMethod]
    public async Task CourseService_GetCourse_ReturnsFailWhenCourseIsSoftDeleted()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        Result<bool> deleteResult = await service.DeleteCourseAsync(course.Id, AdminScope);
        Assert.IsTrue(deleteResult.Success, deleteResult.Error);

        Result<Course> result = await service.GetCourseAsync(course.Id, AdminScope);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CourseService_DeleteQuestion_SetsInactiveAndExcludesFromGetCourse()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        Result<CourseQuestion> created = await service.SaveQuestionAsync(BuildQuestion(course.Id, ("A", true), ("B", false)));

        Result<bool> deleteResult = await service.DeleteQuestionAsync(created.Data!.Id);
        Assert.IsTrue(deleteResult.Success, deleteResult.Error);

        Result<Course> reloaded = await service.GetCourseAsync(course.Id, AdminScope);
        Assert.IsTrue(reloaded.Success, reloaded.Error);
        Assert.HasCount(0, reloaded.Data!.Questions);
    }

    [TestMethod]
    public async Task CourseService_ReorderSections_AppliesNewSortOrderFromGivenIdSequence()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        Result<CourseSection> first = await service.SaveSectionAsync(new CourseSection { Id = Guid.Empty, CourseId = course.Id, Title = "Shoes" });
        Result<CourseSection> second = await service.SaveSectionAsync(new CourseSection { Id = Guid.Empty, CourseId = course.Id, Title = "Jewellery" });

        Result<bool> reorderResult = await service.ReorderSectionsAsync(course.Id, [second.Data!.Id, first.Data!.Id]);
        Assert.IsTrue(reorderResult.Success, reorderResult.Error);

        Result<Course> reloaded = await service.GetCourseAsync(course.Id, AdminScope);
        Assert.IsTrue(reloaded.Success, reloaded.Error);
        Assert.AreEqual("Jewellery", reloaded.Data!.Sections[0].Title);
        Assert.AreEqual("Shoes", reloaded.Data!.Sections[1].Title);
    }

    [TestMethod]
    public async Task CourseService_ReorderQuestions_AppliesNewSortOrderFromGivenIdSequence()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        Result<CourseQuestion> first = await service.SaveQuestionAsync(BuildQuestion(course.Id, ("A", true), ("B", false)));
        Result<CourseQuestion> second = await service.SaveQuestionAsync(BuildQuestion(course.Id, ("C", true), ("D", false)));

        Result<bool> reorderResult = await service.ReorderQuestionsAsync(course.Id, [second.Data!.Id, first.Data!.Id]);
        Assert.IsTrue(reorderResult.Success, reorderResult.Error);

        Result<Course> reloaded = await service.GetCourseAsync(course.Id, AdminScope);
        Assert.IsTrue(reloaded.Success, reloaded.Error);
        Assert.AreEqual(second.Data!.Id, reloaded.Data!.Questions[0].Id);
        Assert.AreEqual(first.Data!.Id, reloaded.Data!.Questions[1].Id);
    }

    [TestMethod]
    public async Task CourseService_SaveCourse_AdminUpdate_CanReassignLocationId()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);

        Guid courseId = Guid.NewGuid();
        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = courseId, Name = "Induction", PassMarkPercent = 80, IsActive = true, LocationId = ipswich.Id });
            await ctx.SaveChangesAsync();
        }

        Result<Course> result = await service.SaveCourseAsync(
            new Course { Id = courseId, Name = "Induction", PassMarkPercent = 80, LocationId = wisbech.Id }, AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(wisbech.Id, result.Data!.LocationId);

        await using ApplicationDbContext verifyCtx = new(options);
        Course persisted = await verifyCtx.Courses.SingleAsync(x => x.Id == courseId);
        Assert.AreEqual(wisbech.Id, persisted.LocationId);
    }

    [TestMethod]
    public async Task CourseService_SaveCourse_AdminUpdate_CanAssignLocationToACourseThatHasNone()
    {
        // The gap this closes: courses created through the "New Course" flow land with a null
        // LocationId, which makes them invisible to every non-Admin. An Admin must be able to
        // backfill one through the editor.
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        (Location ipswich, _) = await SeedTwoLocationsAsync(options);

        Guid courseId = Guid.NewGuid();
        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = courseId, Name = "Orphan", PassMarkPercent = 80, IsActive = true, LocationId = null });
            await ctx.SaveChangesAsync();
        }

        Result<Course> result = await service.SaveCourseAsync(
            new Course { Id = courseId, Name = "Orphan", PassMarkPercent = 80, LocationId = ipswich.Id }, AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(ipswich.Id, result.Data!.LocationId);
    }

    [TestMethod]
    public async Task CourseService_SaveCourse_NonAdminUpdate_IgnoresSuppliedLocationId()
    {
        // The tamper guard on update: a non-Admin editing their own course cannot re-home it,
        // even by posting a different LocationId.
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);

        Guid courseId = Guid.NewGuid();
        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = courseId, Name = "Ipswich Course", PassMarkPercent = 80, IsActive = true, LocationId = ipswich.Id });
            await ctx.SaveChangesAsync();
        }

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<Course> result = await service.SaveCourseAsync(
            new Course { Id = courseId, Name = "Renamed", PassMarkPercent = 80, LocationId = wisbech.Id }, ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(ipswich.Id, result.Data!.LocationId);

        await using ApplicationDbContext verifyCtx = new(options);
        Course persisted = await verifyCtx.Courses.SingleAsync(x => x.Id == courseId);
        Assert.AreEqual(ipswich.Id, persisted.LocationId);
        Assert.AreEqual("Renamed", persisted.Name);
    }
}
