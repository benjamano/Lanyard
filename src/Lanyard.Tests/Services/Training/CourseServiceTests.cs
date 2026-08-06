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

        Result<List<Course>> result = await service.GetCoursesAsync();

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

        Result<Course> result = await service.SaveCourseAsync(newCourse);

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

        Result<Course> result = await service.SaveCourseAsync(course);

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

        Result<Course> result = await service.SaveCourseAsync(new Course { Id = Guid.Empty, Name = "   ", PassMarkPercent = 80 });

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CourseService_DeleteCourse_SetsInactive()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        Result<bool> result = await service.DeleteCourseAsync(course.Id);

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

        Result<Course> result = await service.GetCourseAsync(Guid.NewGuid());

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

        Result<Course> reloaded = await service.GetCourseAsync(course.Id);
        Assert.IsTrue(reloaded.Success, reloaded.Error);
        Assert.HasCount(0, reloaded.Data!.Sections);
    }
}
