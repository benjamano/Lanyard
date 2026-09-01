using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Training;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Training;

[TestClass]
public class TrainingAnalyticsServiceTests
{
    private static readonly LocationScope AdminScope = new(true, null, null, null);

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static TrainingAnalyticsService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new TrainingAnalyticsService(factoryMock.Object);
    }

    private static async Task<(Location ipswich, Location wisbech)> SeedTwoLocationsAsync(DbContextOptions<ApplicationDbContext> options)
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
    public async Task GetCourseCompletionSummaryAsync_NonAdmin_CountsOnlyOwnLocationsAssignments()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        TrainingAnalyticsService service = GetService(options);

        Guid courseId = Guid.NewGuid();
        int ipswichLocationId, wisbechLocationId;

        await using (ApplicationDbContext ctx = new(options))
        {
            Company company = new() { Name = "Play2Day", IsActive = true };
            ctx.Companies.Add(company);
            await ctx.SaveChangesAsync();

            Location ipswich = new() { CompanyId = company.Id, Name = "Ipswich", IsActive = true };
            Location wisbech = new() { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.AddRange(ipswich, wisbech);
            await ctx.SaveChangesAsync();
            ipswichLocationId = ipswich.Id;
            wisbechLocationId = wisbech.Id;

            ctx.Courses.Add(new Course { Id = courseId, Name = "COSHH", PassMarkPercent = 80, IsActive = true, LocationId = ipswich.Id, IsShared = true });
            ctx.CourseAssignments.Add(new CourseAssignment { Id = Guid.NewGuid(), CourseId = courseId, UserId = "ipswich-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = ipswich.Id });
            ctx.CourseAssignments.Add(new CourseAssignment { Id = Guid.NewGuid(), CourseId = courseId, UserId = "wisbech-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = wisbech.Id });
            await ctx.SaveChangesAsync();
        }

        LocationScope ipswichScope = new(false, ipswichLocationId, 1, "Play2Day Ipswich");
        Result<CourseCompletionSummary> result = await service.GetCourseCompletionSummaryAsync(courseId, ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.Data!.TotalAssignments);
    }

    [TestMethod]
    public async Task GetCourseCompletionSummaryAsync_Admin_CountsEveryLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        TrainingAnalyticsService service = GetService(options);

        Guid courseId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            Company company = new() { Name = "Play2Day", IsActive = true };
            ctx.Companies.Add(company);
            await ctx.SaveChangesAsync();

            Location ipswich = new() { CompanyId = company.Id, Name = "Ipswich", IsActive = true };
            Location wisbech = new() { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.AddRange(ipswich, wisbech);
            await ctx.SaveChangesAsync();

            ctx.Courses.Add(new Course { Id = courseId, Name = "COSHH", PassMarkPercent = 80, IsActive = true, LocationId = ipswich.Id, IsShared = true });
            ctx.CourseAssignments.Add(new CourseAssignment { Id = Guid.NewGuid(), CourseId = courseId, UserId = "ipswich-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = ipswich.Id });
            ctx.CourseAssignments.Add(new CourseAssignment { Id = Guid.NewGuid(), CourseId = courseId, UserId = "wisbech-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = wisbech.Id });
            await ctx.SaveChangesAsync();
        }

        Result<CourseCompletionSummary> result = await service.GetCourseCompletionSummaryAsync(courseId, AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(2, result.Data!.TotalAssignments);
    }

    [TestMethod]
    public async Task GetTopScoringTraineesAsync_NonAdmin_ReturnsOnlyOwnLocationTrainees()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        TrainingAnalyticsService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);

        Guid courseId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = courseId, Name = "COSHH", PassMarkPercent = 80, IsActive = true, LocationId = ipswich.Id, IsShared = true });

            CourseAssignment ipswichAssignment = new() { Id = Guid.NewGuid(), CourseId = courseId, UserId = "ipswich-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = ipswich.Id };
            ipswichAssignment.Attempts.Add(new CourseQuizAttempt { Id = Guid.NewGuid(), AssignmentId = ipswichAssignment.Id, AttemptNumber = 1, ScorePercent = 90, Passed = true, SubmittedDate = DateTime.UtcNow });
            ctx.CourseAssignments.Add(ipswichAssignment);

            CourseAssignment wisbechAssignment = new() { Id = Guid.NewGuid(), CourseId = courseId, UserId = "wisbech-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = wisbech.Id };
            wisbechAssignment.Attempts.Add(new CourseQuizAttempt { Id = Guid.NewGuid(), AssignmentId = wisbechAssignment.Id, AttemptNumber = 1, ScorePercent = 100, Passed = true, SubmittedDate = DateTime.UtcNow });
            ctx.CourseAssignments.Add(wisbechAssignment);

            await ctx.SaveChangesAsync();
        }

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<List<TraineeScoreRankingRow>> result = await service.GetTopScoringTraineesAsync(courseId, ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, result.Data!);
        Assert.AreEqual("ipswich-staff", result.Data![0].UserId);
    }

    [TestMethod]
    public async Task GetTopScoringTraineesAsync_Admin_ReturnsEveryLocationsTrainees()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        TrainingAnalyticsService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);

        Guid courseId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = courseId, Name = "COSHH", PassMarkPercent = 80, IsActive = true, LocationId = ipswich.Id, IsShared = true });

            CourseAssignment ipswichAssignment = new() { Id = Guid.NewGuid(), CourseId = courseId, UserId = "ipswich-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = ipswich.Id };
            ipswichAssignment.Attempts.Add(new CourseQuizAttempt { Id = Guid.NewGuid(), AssignmentId = ipswichAssignment.Id, AttemptNumber = 1, ScorePercent = 90, Passed = true, SubmittedDate = DateTime.UtcNow });
            ctx.CourseAssignments.Add(ipswichAssignment);

            CourseAssignment wisbechAssignment = new() { Id = Guid.NewGuid(), CourseId = courseId, UserId = "wisbech-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = wisbech.Id };
            wisbechAssignment.Attempts.Add(new CourseQuizAttempt { Id = Guid.NewGuid(), AssignmentId = wisbechAssignment.Id, AttemptNumber = 1, ScorePercent = 100, Passed = true, SubmittedDate = DateTime.UtcNow });
            ctx.CourseAssignments.Add(wisbechAssignment);

            await ctx.SaveChangesAsync();
        }

        Result<List<TraineeScoreRankingRow>> result = await service.GetTopScoringTraineesAsync(courseId, AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(2, result.Data!);
    }

    [TestMethod]
    public async Task GetFastestCompletionsAsync_NonAdmin_ReturnsOnlyOwnLocationTrainees()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        TrainingAnalyticsService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);

        Guid courseId = Guid.NewGuid();
        DateTime start = DateTime.UtcNow.AddHours(-2);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Courses.Add(new Course { Id = courseId, Name = "COSHH", PassMarkPercent = 80, IsActive = true, LocationId = ipswich.Id, IsShared = true });
            ctx.CourseAssignments.Add(new CourseAssignment { Id = Guid.NewGuid(), CourseId = courseId, UserId = "ipswich-staff", AssignedDate = start, IsActive = true, LocationId = ipswich.Id, StartedDate = start, CompletedDate = start.AddMinutes(10) });
            ctx.CourseAssignments.Add(new CourseAssignment { Id = Guid.NewGuid(), CourseId = courseId, UserId = "wisbech-staff", AssignedDate = start, IsActive = true, LocationId = wisbech.Id, StartedDate = start, CompletedDate = start.AddMinutes(5) });
            await ctx.SaveChangesAsync();
        }

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<List<TraineeTimingRankingRow>> result = await service.GetFastestCompletionsAsync(courseId, ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, result.Data!);
        Assert.AreEqual("ipswich-staff", result.Data![0].UserId);
    }
}
