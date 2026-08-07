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
public class CourseAssignmentServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static CourseAssignmentService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new CourseAssignmentService(factoryMock.Object);
    }

    private static async Task<Course> SeedCourseAsync(DbContextOptions<ApplicationDbContext> options, int passMarkPercent = 80)
    {
        await using ApplicationDbContext ctx = new(options);

        Course course = new()
        {
            Id = Guid.NewGuid(),
            Name = "Play2Day Induction",
            PassMarkPercent = passMarkPercent,
            IsActive = true
        };

        ctx.Courses.Add(course);
        await ctx.SaveChangesAsync();

        return course;
    }

    private static async Task<CourseQuestionOption> SeedQuestionAsync(
        DbContextOptions<ApplicationDbContext> options, Guid courseId, string correctText = "Correct", string wrongText = "Wrong")
    {
        await using ApplicationDbContext ctx = new(options);

        Guid questionId = Guid.NewGuid();

        CourseQuestionOption correctOption = new()
        {
            Id = Guid.NewGuid(),
            QuestionId = questionId,
            OptionText = correctText,
            IsCorrect = true,
            IsActive = true
        };

        CourseQuestion question = new()
        {
            Id = questionId,
            CourseId = courseId,
            QuestionText = "If I am ill I should...",
            IsActive = true,
            Options =
            [
                correctOption,
                new CourseQuestionOption { Id = Guid.NewGuid(), QuestionId = questionId, OptionText = wrongText, IsCorrect = false, IsActive = true }
            ]
        };

        ctx.CourseQuestions.Add(question);
        await ctx.SaveChangesAsync();

        return correctOption;
    }

    private static async Task<UserProfile> SeedUserAsync(DbContextOptions<ApplicationDbContext> options, string userId = "learner-1")
    {
        await using ApplicationDbContext ctx = new(options);

        UserProfile user = new()
        {
            Id = userId,
            UserName = userId,
            FirstName = "Test",
            LastName = "Learner"
        };

        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        return user;
    }

    private static async Task<CourseAssignment> SeedAssignmentAsync(
        DbContextOptions<ApplicationDbContext> options, Guid courseId, string userId,
        DateTime? startedDate = null, DateTime? completedDate = null)
    {
        await using ApplicationDbContext ctx = new(options);

        CourseAssignment assignment = new()
        {
            Id = Guid.NewGuid(),
            CourseId = courseId,
            UserId = userId,
            AssignedDate = DateTime.UtcNow,
            StartedDate = startedDate,
            CompletedDate = completedDate,
            IsActive = true
        };

        ctx.CourseAssignments.Add(assignment);
        await ctx.SaveChangesAsync();

        return assignment;
    }

    [TestMethod]
    public async Task AssignCourseAsync_CreatesAssignment_WhenCourseAndUserExist()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);

        Result<CourseAssignment> result = await service.AssignCourseAsync(course.Id, user.Id, "manager-1", null);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(course.Id, result.Data!.CourseId);
        Assert.AreEqual(user.Id, result.Data!.UserId);
        Assert.AreEqual("manager-1", result.Data!.AssignedByUserId);
        Assert.IsTrue(result.Data!.IsActive);
    }

    [TestMethod]
    public async Task AssignCourseAsync_FailsWhenCourseNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        UserProfile user = await SeedUserAsync(options);

        Result<CourseAssignment> result = await service.AssignCourseAsync(Guid.NewGuid(), user.Id, "manager-1", null);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task AssignCourseAsync_FailsWhenUserNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        Result<CourseAssignment> result = await service.AssignCourseAsync(course.Id, "no-such-user", "manager-1", null);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task AssignCourseAsync_NormalizesDueDateToUtc()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);

        DateTime unspecifiedDueDate = new(2026, 12, 25, 0, 0, 0, DateTimeKind.Unspecified);
        Assert.AreEqual(DateTimeKind.Unspecified, unspecifiedDueDate.Kind);

        Result<CourseAssignment> result = await service.AssignCourseAsync(course.Id, user.Id, "manager-1", unspecifiedDueDate);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Data!.DueDate);
        Assert.AreEqual(DateTimeKind.Utc, result.Data!.DueDate!.Value.Kind);

        await using ApplicationDbContext verifyCtx = new(options);
        CourseAssignment dbAssignment = await verifyCtx.CourseAssignments.SingleAsync(x => x.Id == result.Data!.Id);
        Assert.IsNotNull(dbAssignment.DueDate);
        Assert.AreEqual(DateTimeKind.Utc, dbAssignment.DueDate!.Value.Kind);
    }

    [TestMethod]
    public async Task GetAssignmentsForUserAsync_ReturnsOnlyThatUsersActiveAssignments()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile userA = await SeedUserAsync(options, "user-a");
        UserProfile userB = await SeedUserAsync(options, "user-b");

        await SeedAssignmentAsync(options, course.Id, userA.Id);
        await SeedAssignmentAsync(options, course.Id, userB.Id);

        Result<List<CourseAssignment>> result = await service.GetAssignmentsForUserAsync(userA.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, result.Data!);
        Assert.AreEqual(userA.Id, result.Data![0].UserId);
    }

    [TestMethod]
    public async Task GetAssignmentAsync_FailsWhenRequestingUserIsNotOwner()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile owner = await SeedUserAsync(options, "owner");
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, owner.Id);

        Result<CourseAssignment> result = await service.GetAssignmentAsync(assignment.Id, "someone-else");

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task GetAssignmentAsync_ReturnsCourseWithOrderedSections()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CourseSections.Add(new CourseSection { Id = Guid.NewGuid(), CourseId = course.Id, Title = "Second", SortOrder = 1, IsActive = true });
            ctx.CourseSections.Add(new CourseSection { Id = Guid.NewGuid(), CourseId = course.Id, Title = "First", SortOrder = 0, IsActive = true });
            await ctx.SaveChangesAsync();
        }

        Result<CourseAssignment> result = await service.GetAssignmentAsync(assignment.Id, user.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(2, result.Data!.Course!.Sections);
        Assert.AreEqual("First", result.Data!.Course!.Sections[0].Title);
        Assert.AreEqual("Second", result.Data!.Course!.Sections[1].Title);
    }

    [TestMethod]
    public async Task StartAssignmentAsync_SetsStartedDateOnce()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        Result<CourseAssignment> firstStart = await service.StartAssignmentAsync(assignment.Id, user.Id);
        Assert.IsTrue(firstStart.Success, firstStart.Error);
        DateTime firstStartedDate = firstStart.Data!.StartedDate!.Value;

        await Task.Delay(10);
        Result<CourseAssignment> secondStart = await service.StartAssignmentAsync(assignment.Id, user.Id);

        Assert.IsTrue(secondStart.Success, secondStart.Error);
        Assert.AreEqual(firstStartedDate, secondStart.Data!.StartedDate!.Value);
    }

    [TestMethod]
    public async Task StartAssignmentAsync_FailsWhenRequestingUserIsNotOwner()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile owner = await SeedUserAsync(options, "owner");
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, owner.Id);

        Result<CourseAssignment> result = await service.StartAssignmentAsync(assignment.Id, "someone-else");

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task SubmitQuizAttemptAsync_GradesCorrectlyAndPassesWhenAboveThreshold()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options, passMarkPercent: 80);
        CourseQuestionOption correctOption = await SeedQuestionAsync(options, course.Id);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        Dictionary<Guid, Guid> answers = new() { [correctOption.QuestionId] = correctOption.Id };

        Result<QuizGradeResult> result = await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(100, result.Data!.ScorePercent);
        Assert.IsTrue(result.Data!.Passed);

        await using ApplicationDbContext ctx = new(options);
        CourseAssignment dbAssignment = await ctx.CourseAssignments.SingleAsync(x => x.Id == assignment.Id);
        Assert.IsNotNull(dbAssignment.CompletedDate);
    }

    [TestMethod]
    public async Task SubmitQuizAttemptAsync_FailsBelowThreshold_DoesNotSetCompletedDate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options, passMarkPercent: 80);
        CourseQuestionOption correctOption = await SeedQuestionAsync(options, course.Id);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        CourseQuestionOption wrongOption;
        await using (ApplicationDbContext ctx = new(options))
        {
            wrongOption = await ctx.CourseQuestionOptions.SingleAsync(x => x.QuestionId == correctOption.QuestionId && !x.IsCorrect);
        }

        Dictionary<Guid, Guid> answers = new() { [correctOption.QuestionId] = wrongOption.Id };

        Result<QuizGradeResult> result = await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(0, result.Data!.ScorePercent);
        Assert.IsFalse(result.Data!.Passed);

        await using ApplicationDbContext verifyCtx = new(options);
        CourseAssignment dbAssignment = await verifyCtx.CourseAssignments.SingleAsync(x => x.Id == assignment.Id);
        Assert.IsNull(dbAssignment.CompletedDate);
    }

    [TestMethod]
    public async Task SubmitQuizAttemptAsync_SecondPassingAttempt_DoesNotOverwriteCompletedDate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options, passMarkPercent: 80);
        CourseQuestionOption correctOption = await SeedQuestionAsync(options, course.Id);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);
        Dictionary<Guid, Guid> answers = new() { [correctOption.QuestionId] = correctOption.Id };

        Result<QuizGradeResult> firstAttempt = await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);
        Assert.IsTrue(firstAttempt.Success, firstAttempt.Error);

        DateTime firstCompletedDate;
        await using (ApplicationDbContext ctx = new(options))
        {
            firstCompletedDate = (await ctx.CourseAssignments.SingleAsync(x => x.Id == assignment.Id)).CompletedDate!.Value;
        }

        await Task.Delay(10);
        Result<QuizGradeResult> secondAttempt = await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);
        Assert.IsTrue(secondAttempt.Success, secondAttempt.Error);

        await using ApplicationDbContext verifyCtx = new(options);
        CourseAssignment dbAssignment = await verifyCtx.CourseAssignments.SingleAsync(x => x.Id == assignment.Id);
        Assert.AreEqual(firstCompletedDate, dbAssignment.CompletedDate!.Value);
    }

    [TestMethod]
    public async Task SubmitQuizAttemptAsync_IncrementsAttemptNumberAcrossRetries()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options, passMarkPercent: 80);
        CourseQuestionOption correctOption = await SeedQuestionAsync(options, course.Id);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);
        Dictionary<Guid, Guid> answers = new() { [correctOption.QuestionId] = correctOption.Id };

        Result<QuizGradeResult> firstAttempt = await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);
        Result<QuizGradeResult> secondAttempt = await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);

        Assert.IsTrue(firstAttempt.Success, firstAttempt.Error);
        Assert.IsTrue(secondAttempt.Success, secondAttempt.Error);
        Assert.AreEqual(1, firstAttempt.Data!.AttemptNumber);
        Assert.AreEqual(2, secondAttempt.Data!.AttemptNumber);

        await using ApplicationDbContext ctx = new(options);
        Assert.HasCount(2, await ctx.CourseQuizAttempts.Where(x => x.AssignmentId == assignment.Id).ToListAsync());
    }

    [TestMethod]
    public async Task SubmitQuizAttemptAsync_FailsWhenRequestingUserIsNotOwner()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        CourseQuestionOption correctOption = await SeedQuestionAsync(options, course.Id);
        UserProfile owner = await SeedUserAsync(options, "owner");
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, owner.Id);
        Dictionary<Guid, Guid> answers = new() { [correctOption.QuestionId] = correctOption.Id };

        Result<QuizGradeResult> result = await service.SubmitQuizAttemptAsync(assignment.Id, "someone-else", answers);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task SubmitQuizAttemptAsync_CalculatesFractionalScoreCorrectly()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options, passMarkPercent: 60);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        CourseQuestionOption correctOption1 = await SeedQuestionAsync(options, course.Id, "Correct1", "Wrong1");
        CourseQuestionOption correctOption2 = await SeedQuestionAsync(options, course.Id, "Correct2", "Wrong2");
        CourseQuestionOption correctOption3 = await SeedQuestionAsync(options, course.Id, "Correct3", "Wrong3");

        CourseQuestionOption wrongOption;
        await using (ApplicationDbContext ctx = new(options))
        {
            wrongOption = await ctx.CourseQuestionOptions.SingleAsync(x => x.QuestionId == correctOption3.QuestionId && !x.IsCorrect);
        }

        Dictionary<Guid, Guid> answers = new()
        {
            [correctOption1.QuestionId] = correctOption1.Id,
            [correctOption2.QuestionId] = correctOption2.Id,
            [correctOption3.QuestionId] = wrongOption.Id
        };

        Result<QuizGradeResult> result = await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(67, result.Data!.ScorePercent);
        Assert.IsTrue(result.Data!.Passed);
    }

    [TestMethod]
    public async Task GetAssignmentsForCourseAsync_ReturnsOnlyActiveAssignmentsForThatCourse()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course courseA = await SeedCourseAsync(options);
        Course courseB = await SeedCourseAsync(options, passMarkPercent: 70);
        UserProfile userA = await SeedUserAsync(options, "user-a");
        UserProfile userB = await SeedUserAsync(options, "user-b");

        await SeedAssignmentAsync(options, courseA.Id, userA.Id);
        await SeedAssignmentAsync(options, courseB.Id, userB.Id);

        Result<List<CourseAssignment>> result = await service.GetAssignmentsForCourseAsync(courseA.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, result.Data!);
        Assert.AreEqual(userA.Id, result.Data![0].UserId);
    }

    [TestMethod]
    public async Task AssignCourseToUsersAsync_AssignsToAllNewUsers_ReturnsCorrectCounts()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile userA = await SeedUserAsync(options, "user-a");
        UserProfile userB = await SeedUserAsync(options, "user-b");

        Result<BulkAssignResult> result = await service.AssignCourseToUsersAsync(
            course.Id, [userA.Id, userB.Id], "manager-1", null);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(2, result.Data!.AssignedCount);
        Assert.AreEqual(0, result.Data!.SkippedDuplicateCount);

        await using ApplicationDbContext ctx = new(options);
        Assert.HasCount(2, await ctx.CourseAssignments.Where(x => x.CourseId == course.Id).ToListAsync());
    }

    [TestMethod]
    public async Task AssignCourseToUsersAsync_SkipsAlreadyAssignedUsers_CountsThemAsSkipped()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile userA = await SeedUserAsync(options, "user-a");
        UserProfile userB = await SeedUserAsync(options, "user-b");

        await SeedAssignmentAsync(options, course.Id, userA.Id);

        Result<BulkAssignResult> result = await service.AssignCourseToUsersAsync(
            course.Id, [userA.Id, userB.Id], "manager-1", null);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.Data!.AssignedCount);
        Assert.AreEqual(1, result.Data!.SkippedDuplicateCount);
    }

    [TestMethod]
    public async Task AssignCourseToUsersAsync_FailsWhenCourseNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        UserProfile user = await SeedUserAsync(options);

        Result<BulkAssignResult> result = await service.AssignCourseToUsersAsync(
            Guid.NewGuid(), [user.Id], "manager-1", null);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task AssignCourseToUsersAsync_NormalizesDueDateToEndOfDayUtc()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        DateTime unspecifiedDueDate = new(2026, 12, 25, 0, 0, 0, DateTimeKind.Unspecified);

        Result<BulkAssignResult> result = await service.AssignCourseToUsersAsync(
            course.Id, [user.Id], "manager-1", unspecifiedDueDate);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        CourseAssignment dbAssignment = await ctx.CourseAssignments.SingleAsync(x => x.CourseId == course.Id);
        Assert.AreEqual(DateTimeKind.Utc, dbAssignment.DueDate!.Value.Kind);
        Assert.AreEqual(25, dbAssignment.DueDate!.Value.Day);
        Assert.AreEqual(23, dbAssignment.DueDate!.Value.Hour);
        Assert.AreEqual(59, dbAssignment.DueDate!.Value.Minute);
    }

    [TestMethod]
    public async Task UpdateAssignmentDueDateAsync_UpdatesExistingDueDate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        Result<CourseAssignment> result = await service.UpdateAssignmentDueDateAsync(
            assignment.Id, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Unspecified));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Data!.DueDate);
        Assert.AreEqual(1, result.Data!.DueDate!.Value.Day);
        Assert.AreEqual(DateTimeKind.Utc, result.Data!.DueDate!.Value.Kind);
    }

    [TestMethod]
    public async Task UpdateAssignmentDueDateAsync_CanClearDueDateToNull()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        Result<CourseAssignment> withDueDate = await service.UpdateAssignmentDueDateAsync(
            assignment.Id, DateTime.UtcNow.AddDays(5));
        Assert.IsTrue(withDueDate.Success, withDueDate.Error);

        Result<CourseAssignment> cleared = await service.UpdateAssignmentDueDateAsync(assignment.Id, null);

        Assert.IsTrue(cleared.Success, cleared.Error);
        Assert.IsNull(cleared.Data!.DueDate);
    }

    [TestMethod]
    public async Task UpdateAssignmentDueDateAsync_FailsWhenAssignmentNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);

        Result<CourseAssignment> result = await service.UpdateAssignmentDueDateAsync(Guid.NewGuid(), null);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task UnassignAsync_SetsInactive()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        Result<bool> result = await service.UnassignAsync(assignment.Id);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        CourseAssignment dbAssignment = await ctx.CourseAssignments.SingleAsync(x => x.Id == assignment.Id);
        Assert.IsFalse(dbAssignment.IsActive);
    }

    [TestMethod]
    public async Task UnassignAsync_FailsWhenAssignmentNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);

        Result<bool> result = await service.UnassignAsync(Guid.NewGuid());

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task UnassignAsync_ExcludesFromGetAssignmentsForCourseAsync()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        Result<bool> unassignResult = await service.UnassignAsync(assignment.Id);
        Assert.IsTrue(unassignResult.Success, unassignResult.Error);

        Result<List<CourseAssignment>> result = await service.GetAssignmentsForCourseAsync(course.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(0, result.Data!);
    }
}
