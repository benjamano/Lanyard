using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Training;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Training;

[TestClass]
public class CourseAssignmentServiceTests
{
    private static readonly LocationScope AdminScope = new(true, null, null, null);

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static CourseAssignmentService GetService(
        DbContextOptions<ApplicationDbContext> options,
        Mock<IEmailService>? emailServiceMock = null,
        Mock<ITrainingBrandingResolver>? brandingResolverMock = null,
        Mock<ICertificateService>? certificateServiceMock = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<IEmailService> resolvedEmailServiceMock = emailServiceMock ?? new Mock<IEmailService>();
        resolvedEmailServiceMock
            .Setup(e => e.SendTrainingAssignedEmailAsync(
                It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()))
            .ReturnsAsync(Result<bool>.Ok(true));
        resolvedEmailServiceMock
            .Setup(e => e.SendCourseCompletionCertificateEmailAsync(
                It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        Mock<ITrainingBrandingResolver> resolvedBrandingResolverMock = brandingResolverMock ?? new Mock<ITrainingBrandingResolver>();
        resolvedBrandingResolverMock
            .Setup(b => b.ResolveAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(TrainingBranding.Default);

        Mock<ICertificateService> resolvedCertificateServiceMock = certificateServiceMock ?? new Mock<ICertificateService>();
        resolvedCertificateServiceMock
            .Setup(c => c.GenerateCertificatePdfAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<byte[]>.Ok([1, 2, 3]));

        return new CourseAssignmentService(
            factoryMock.Object,
            resolvedEmailServiceMock.Object,
            Options.Create(new EmailOptions { PublicBaseUrl = "https://lanyard.example.com" }),
            resolvedBrandingResolverMock.Object,
            resolvedCertificateServiceMock.Object,
            NullLogger<CourseAssignmentService>.Instance);
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

    private static async Task SeedUserInLocationAsync(DbContextOptions<ApplicationDbContext> options, string userId, int locationId)
    {
        await using ApplicationDbContext ctx = new(options);
        ctx.Users.Add(new UserProfile { Id = userId, UserName = userId, FirstName = "Test", LastName = "User" });
        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = userId, LocationId = locationId, CreateDate = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
    }

    private static async Task<Course> SeedCourseAsync(
        DbContextOptions<ApplicationDbContext> options, int passMarkPercent = 80,
        int? locationId = null, bool isShared = false, string name = "Play2Day Induction")
    {
        await using ApplicationDbContext ctx = new(options);

        Course course = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            PassMarkPercent = passMarkPercent,
            IsActive = true,
            LocationId = locationId,
            IsShared = isShared
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
        DateTime? startedDate = null, DateTime? completedDate = null,
        DateTime? dueDate = null, DateTime? dueSoonReminderSentDate = null, bool isActive = true,
        int? locationId = null)
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
            DueDate = dueDate,
            DueSoonReminderSentDate = dueSoonReminderSentDate,
            IsActive = isActive,
            LocationId = locationId
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

        Result<CourseAssignment> result = await service.AssignCourseAsync(course.Id, user.Id, "manager-1", null, AdminScope);

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

        Result<CourseAssignment> result = await service.AssignCourseAsync(Guid.NewGuid(), user.Id, "manager-1", null, AdminScope);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task AssignCourseAsync_FailsWhenUserNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);

        Result<CourseAssignment> result = await service.AssignCourseAsync(course.Id, "no-such-user", "manager-1", null, AdminScope);

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

        Result<CourseAssignment> result = await service.AssignCourseAsync(course.Id, user.Id, "manager-1", unspecifiedDueDate, AdminScope);

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

    // The three tests below cover the manager-facing "By Person" training tab, which passes a
    // LocationScope so one location's manager cannot read another location's training records.
    // The null-scope case guards the opposite path - MyTraining/OutstandingTrainingCard call this
    // for the signed-in user's own record, where every location's assignments must still show.
    private static async Task<(string userId, Location ipswich, Location wisbech)> SeedUserWithAssignmentsInTwoLocationsAsync(
        DbContextOptions<ApplicationDbContext> options)
    {
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);

        Course ipswichCourse = await SeedCourseAsync(options, locationId: ipswich.Id, name: "Ipswich Induction");
        Course wisbechCourse = await SeedCourseAsync(options, locationId: wisbech.Id, name: "Wisbech Induction");

        UserProfile user = await SeedUserAsync(options, "multi-location-user");

        await SeedAssignmentAsync(options, ipswichCourse.Id, user.Id, locationId: ipswich.Id);
        await SeedAssignmentAsync(options, wisbechCourse.Id, user.Id, locationId: wisbech.Id);

        return (user.Id, ipswich, wisbech);
    }

    [TestMethod]
    public async Task GetAssignmentsForUserAsync_WithoutScope_ReturnsEveryLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (string userId, _, _) = await SeedUserWithAssignmentsInTwoLocationsAsync(options);

        Result<List<CourseAssignment>> result = await service.GetAssignmentsForUserAsync(userId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(2, result.Data!);
    }

    [TestMethod]
    public async Task GetAssignmentsForUserAsync_WithLocationScope_ExcludesOtherLocations()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (string userId, _, Location wisbech) = await SeedUserWithAssignmentsInTwoLocationsAsync(options);

        LocationScope wisbechScope = new(false, wisbech.Id, wisbech.CompanyId, wisbech.Name);

        Result<List<CourseAssignment>> result = await service.GetAssignmentsForUserAsync(userId, wisbechScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, result.Data!);
        Assert.AreEqual(wisbech.Id, result.Data![0].LocationId);
    }

    [TestMethod]
    public async Task GetAssignmentsForUserAsync_WithAdminScope_ReturnsEveryLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (string userId, _, _) = await SeedUserWithAssignmentsInTwoLocationsAsync(options);

        Result<List<CourseAssignment>> result = await service.GetAssignmentsForUserAsync(userId, AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(2, result.Data!);
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
    public async Task SubmitQuizAttemptAsync_FirstPass_SendsCertificateEmailExactlyOnce()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IEmailService> emailServiceMock = new();
        CourseAssignmentService service = GetService(options, emailServiceMock);
        Course course = await SeedCourseAsync(options, passMarkPercent: 80);
        CourseQuestionOption correctOption = await SeedQuestionAsync(options, course.Id);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        Dictionary<Guid, Guid> answers = new() { [correctOption.QuestionId] = correctOption.Id };

        Result<QuizGradeResult> result = await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);

        Assert.IsTrue(result.Success, result.Error);
        emailServiceMock.Verify(e => e.SendCourseCompletionCertificateEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string>()), Times.Once());
    }

    [TestMethod]
    public async Task SubmitQuizAttemptAsync_RetakeOfCompletedCourse_DoesNotResendCertificateEmail()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IEmailService> emailServiceMock = new();
        CourseAssignmentService service = GetService(options, emailServiceMock);
        Course course = await SeedCourseAsync(options, passMarkPercent: 80);
        CourseQuestionOption correctOption = await SeedQuestionAsync(options, course.Id);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        Dictionary<Guid, Guid> answers = new() { [correctOption.QuestionId] = correctOption.Id };

        await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);
        await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);

        // Still Once across both passes - the assignment was only ever completed once.
        emailServiceMock.Verify(e => e.SendCourseCompletionCertificateEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string>()), Times.Once());
    }

    [TestMethod]
    public async Task SubmitQuizAttemptAsync_FailedAttempt_DoesNotSendCertificateEmail()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IEmailService> emailServiceMock = new();
        CourseAssignmentService service = GetService(options, emailServiceMock);
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

        await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);

        emailServiceMock.Verify(e => e.SendCourseCompletionCertificateEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string>()), Times.Never());
    }

    [TestMethod]
    public async Task SubmitQuizAttemptAsync_CertificateGenerationFails_StillReturnsQuizResult()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<ICertificateService> certificateServiceMock = new();
        certificateServiceMock
            .Setup(c => c.GenerateCertificatePdfAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<byte[]>.Fail("renderer exploded"));

        CourseAssignmentService service = GetService(options, certificateServiceMock: certificateServiceMock);
        Course course = await SeedCourseAsync(options, passMarkPercent: 80);
        CourseQuestionOption correctOption = await SeedQuestionAsync(options, course.Id);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        Dictionary<Guid, Guid> answers = new() { [correctOption.QuestionId] = correctOption.Id };

        Result<QuizGradeResult> result = await service.SubmitQuizAttemptAsync(assignment.Id, user.Id, answers);

        // A certificate problem must never cost the learner their pass.
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(100, result.Data!.ScorePercent);
        Assert.IsTrue(result.Data!.Passed);

        await using ApplicationDbContext ctx = new(options);
        CourseAssignment dbAssignment = await ctx.CourseAssignments.SingleAsync(x => x.Id == assignment.Id);
        Assert.IsNotNull(dbAssignment.CompletedDate);
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

        Result<List<CourseAssignment>> result = await service.GetAssignmentsForCourseAsync(courseA.Id, AdminScope);

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
            course.Id, [userA.Id, userB.Id], "manager-1", null, AdminScope);

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
            course.Id, [userA.Id, userB.Id], "manager-1", null, AdminScope);

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
            Guid.NewGuid(), [user.Id], "manager-1", null, AdminScope);

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
            course.Id, [user.Id], "manager-1", unspecifiedDueDate, AdminScope);

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

        Result<List<CourseAssignment>> result = await service.GetAssignmentsForCourseAsync(course.Id, AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(0, result.Data!);
    }

    [TestMethod]
    public async Task UnassignAllForUserAsync_DeactivatesAllActiveAssignmentsForThatUser()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course courseA = await SeedCourseAsync(options);
        Course courseB = await SeedCourseAsync(options, passMarkPercent: 70);
        UserProfile user = await SeedUserAsync(options);
        UserProfile otherUser = await SeedUserAsync(options, "other-user");

        await SeedAssignmentAsync(options, courseA.Id, user.Id);
        await SeedAssignmentAsync(options, courseB.Id, user.Id);
        await SeedAssignmentAsync(options, courseA.Id, otherUser.Id);

        Result<int> result = await service.UnassignAllForUserAsync(user.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(2, result.Data);

        await using ApplicationDbContext ctx = new(options);
        List<CourseAssignment> userAssignments = await ctx.CourseAssignments.Where(x => x.UserId == user.Id).ToListAsync();
        Assert.HasCount(2, userAssignments);
        Assert.IsTrue(userAssignments.All(x => !x.IsActive));

        CourseAssignment otherAssignment = await ctx.CourseAssignments.SingleAsync(x => x.UserId == otherUser.Id);
        Assert.IsTrue(otherAssignment.IsActive);
    }

    [TestMethod]
    public async Task UnassignAllForUserAsync_ReturnsZero_WhenUserHasNoAssignments()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);

        Result<int> result = await service.UnassignAllForUserAsync("no-such-user");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(0, result.Data);
    }

    [TestMethod]
    public async Task RecordSectionTransitionAsync_RevisitingSectionAfterQuiz_DoesNotExtendItsRecordedDuration()
    {
        // Reproduces the reported bug: a learner reads the last section, moves into the
        // quiz, fails, clicks Previous back into that section, then clicks Next into the
        // quiz again. The section's LeftDate must stay pinned to the FIRST departure -
        // otherwise the entire time spent on the failed quiz attempt gets counted as time
        // spent reading the section.
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id);

        Guid sectionId = Guid.NewGuid();
        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CourseSections.Add(new CourseSection { Id = sectionId, CourseId = course.Id, Title = "Only Section", SortOrder = 0, IsActive = true });
            await ctx.SaveChangesAsync();
        }

        DateTime enteredSection = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        DateTime leftForQuiz = enteredSection.AddMinutes(2);
        DateTime backFromQuizAfterFailing = leftForQuiz.AddMinutes(10);
        DateTime leftForQuizAgain = backFromQuizAfterFailing.AddMinutes(1);

        // Arrive at the section.
        await service.RecordSectionTransitionAsync(assignment.Id, user.Id, null, sectionId, enteredSection);
        // Leave the section for the quiz (genuine reading time: 2 minutes).
        await service.RecordSectionTransitionAsync(assignment.Id, user.Id, sectionId, null, leftForQuiz);
        // Fail the quiz, click Previous back into the section.
        await service.RecordSectionTransitionAsync(assignment.Id, user.Id, null, sectionId, backFromQuizAfterFailing);
        // Click Next back into the quiz to retry.
        await service.RecordSectionTransitionAsync(assignment.Id, user.Id, sectionId, null, leftForQuizAgain);

        await using ApplicationDbContext verifyCtx = new(options);
        CourseSectionProgress progress = await verifyCtx.CourseSectionProgresses.SingleAsync(x => x.AssignmentId == assignment.Id && x.SectionId == sectionId);

        Assert.AreEqual(enteredSection, progress.EnteredDate);
        Assert.AreEqual(leftForQuiz, progress.LeftDate);
    }

    [TestMethod]
    public async Task AssignCourseAsync_NonAdmin_SetsAssignmentLocationToActingManagersLocationNotCoursesLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);
        Course sharedCourse = await SeedCourseAsync(options, locationId: wisbech.Id, isShared: true, name: "COSHH");
        await SeedUserInLocationAsync(options, "staff-1", ipswich.Id);

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<CourseAssignment> result = await service.AssignCourseAsync(sharedCourse.Id, "staff-1", "manager-1", null, ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(ipswich.Id, result.Data!.LocationId);
    }

    [TestMethod]
    public async Task AssignCourseAsync_NonAdmin_AssigningToUserOutsideTheirLocation_Fails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);
        Course course = await SeedCourseAsync(options, locationId: ipswich.Id);
        await SeedUserInLocationAsync(options, "staff-1", wisbech.Id);

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<CourseAssignment> result = await service.AssignCourseAsync(course.Id, "staff-1", "manager-1", null, ipswichScope);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("This user is not a member of your location.", result.Error);
    }

    [TestMethod]
    public async Task GetAssignmentsForCourseAsync_SharedCourse_NonAdminSeesOnlyOwnLocationsAssignees()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);
        Course sharedCourse = await SeedCourseAsync(options, locationId: ipswich.Id, isShared: true, name: "COSHH");

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CourseAssignments.Add(new CourseAssignment { Id = Guid.NewGuid(), CourseId = sharedCourse.Id, UserId = "ipswich-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = ipswich.Id });
            ctx.CourseAssignments.Add(new CourseAssignment { Id = Guid.NewGuid(), CourseId = sharedCourse.Id, UserId = "wisbech-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = wisbech.Id });
            await ctx.SaveChangesAsync();
        }

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<List<CourseAssignment>> result = await service.GetAssignmentsForCourseAsync(sharedCourse.Id, ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, result.Data!);
        Assert.AreEqual("ipswich-staff", result.Data![0].UserId);
    }

    [TestMethod]
    public async Task GetAssignmentsForCourseAsync_Admin_SeesAllLocations()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);
        Course sharedCourse = await SeedCourseAsync(options, locationId: ipswich.Id, isShared: true, name: "COSHH");

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CourseAssignments.Add(new CourseAssignment { Id = Guid.NewGuid(), CourseId = sharedCourse.Id, UserId = "ipswich-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = ipswich.Id });
            ctx.CourseAssignments.Add(new CourseAssignment { Id = Guid.NewGuid(), CourseId = sharedCourse.Id, UserId = "wisbech-staff", AssignedDate = DateTime.UtcNow, IsActive = true, LocationId = wisbech.Id });
            await ctx.SaveChangesAsync();
        }

        Result<List<CourseAssignment>> result = await service.GetAssignmentsForCourseAsync(sharedCourse.Id, AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(2, result.Data!);
    }

    [TestMethod]
    public async Task AssignCourseToUsersAsync_NonAdmin_FiltersOutUsersOutsideActingManagersLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);
        Course sharedCourse = await SeedCourseAsync(options, locationId: ipswich.Id, isShared: true, name: "COSHH");
        await SeedUserInLocationAsync(options, "ipswich-staff-1", ipswich.Id);
        await SeedUserInLocationAsync(options, "ipswich-staff-2", ipswich.Id);
        await SeedUserInLocationAsync(options, "wisbech-staff-1", wisbech.Id);

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<BulkAssignResult> result = await service.AssignCourseToUsersAsync(
            sharedCourse.Id, ["ipswich-staff-1", "ipswich-staff-2", "wisbech-staff-1"], "manager-1", null, ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(2, result.Data!.AssignedCount);
        Assert.AreEqual(0, result.Data!.SkippedDuplicateCount);
        // The filtered-out Wisbech user is reported rather than silently vanishing.
        Assert.AreEqual(1, result.Data!.SkippedOutsideLocationCount);

        await using ApplicationDbContext ctx = new(options);
        List<CourseAssignment> assignments = await ctx.CourseAssignments.Where(x => x.CourseId == sharedCourse.Id).ToListAsync();
        Assert.HasCount(2, assignments);
        Assert.IsTrue(assignments.All(x => x.UserId != "wisbech-staff-1"));
        Assert.IsTrue(assignments.All(x => x.LocationId == ipswich.Id));
    }

    [TestMethod]
    public async Task GetCourseTimingSummaryAsync_NonAdmin_OnlyIncludesOwnLocationsAssignments()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);
        Course sharedCourse = await SeedCourseAsync(options, locationId: ipswich.Id, isShared: true, name: "COSHH");

        DateTime started = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        DateTime completed = started.AddMinutes(30);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CourseAssignments.Add(new CourseAssignment
            {
                Id = Guid.NewGuid(), CourseId = sharedCourse.Id, UserId = "ipswich-staff", AssignedDate = started,
                StartedDate = started, CompletedDate = completed, IsActive = true, LocationId = ipswich.Id
            });
            ctx.CourseAssignments.Add(new CourseAssignment
            {
                Id = Guid.NewGuid(), CourseId = sharedCourse.Id, UserId = "wisbech-staff", AssignedDate = started,
                StartedDate = started, CompletedDate = completed, IsActive = true, LocationId = wisbech.Id
            });
            await ctx.SaveChangesAsync();
        }

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<CourseTimingSummary> result = await service.GetCourseTimingSummaryAsync(sharedCourse.Id, ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.Data!.CompletedCount);
    }

    [TestMethod]
    public async Task AssignCourseAsync_NonAdmin_CourseOutsideTheirScope_IsRejected()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);
        Course wisbechCourse = await SeedCourseAsync(options, locationId: wisbech.Id, name: "Wisbech Only");
        await SeedUserInLocationAsync(options, "ipswich-staff-1", ipswich.Id);

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<CourseAssignment> result = await service.AssignCourseAsync(
            wisbechCourse.Id, "ipswich-staff-1", "manager-1", null, ipswichScope);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("You do not have access to this course.", result.Error);

        await using ApplicationDbContext ctx = new(options);
        Assert.IsEmpty(await ctx.CourseAssignments.Where(x => x.CourseId == wisbechCourse.Id).ToListAsync());
    }

    [TestMethod]
    public async Task AssignCourseAsync_NonAdmin_SharedCourseFromSiblingLocation_IsAllowed()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);
        Course sharedCourse = await SeedCourseAsync(options, locationId: wisbech.Id, isShared: true, name: "COSHH");
        await SeedUserInLocationAsync(options, "ipswich-staff-1", ipswich.Id);

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<CourseAssignment> result = await service.AssignCourseAsync(
            sharedCourse.Id, "ipswich-staff-1", "manager-1", null, ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(ipswich.Id, result.Data!.LocationId);
    }

    [TestMethod]
    public async Task AssignCourseToUsersAsync_NonAdmin_CourseOutsideTheirScope_IsRejected()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);
        Course wisbechCourse = await SeedCourseAsync(options, locationId: wisbech.Id, name: "Wisbech Only");
        await SeedUserInLocationAsync(options, "ipswich-staff-1", ipswich.Id);

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<BulkAssignResult> result = await service.AssignCourseToUsersAsync(
            wisbechCourse.Id, ["ipswich-staff-1"], "manager-1", null, ipswichScope);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("You do not have access to this course.", result.Error);

        await using ApplicationDbContext ctx = new(options);
        Assert.IsEmpty(await ctx.CourseAssignments.Where(x => x.CourseId == wisbechCourse.Id).ToListAsync());
    }

    [TestMethod]
    public async Task AssignCourseToUsersAsync_NonAdmin_ReportsOutsideLocationSkipsSeparatelyFromDuplicates()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        (Location ipswich, Location wisbech) = await SeedTwoLocationsAsync(options);
        Course sharedCourse = await SeedCourseAsync(options, locationId: ipswich.Id, isShared: true, name: "COSHH");
        await SeedUserInLocationAsync(options, "ipswich-staff-1", ipswich.Id);
        await SeedUserInLocationAsync(options, "ipswich-staff-2", ipswich.Id);
        await SeedUserInLocationAsync(options, "wisbech-staff-1", wisbech.Id);
        await SeedUserInLocationAsync(options, "wisbech-staff-2", wisbech.Id);

        // ipswich-staff-2 already holds this course, so it is an in-scope duplicate. Both
        // Wisbech users were requested but are outside the acting manager's location.
        await SeedAssignmentAsync(options, sharedCourse.Id, "ipswich-staff-2");

        LocationScope ipswichScope = new(false, ipswich.Id, ipswich.CompanyId, "Play2Day Ipswich");
        Result<BulkAssignResult> result = await service.AssignCourseToUsersAsync(
            sharedCourse.Id,
            ["ipswich-staff-1", "ipswich-staff-2", "wisbech-staff-1", "wisbech-staff-2"],
            "manager-1", null, ipswichScope);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.Data!.AssignedCount);
        Assert.AreEqual(1, result.Data!.SkippedDuplicateCount);
        Assert.AreEqual(2, result.Data!.SkippedOutsideLocationCount);

        // Every requested user is now accounted for by exactly one of the three counts.
        Assert.AreEqual(
            4,
            result.Data!.AssignedCount + result.Data!.SkippedDuplicateCount + result.Data!.SkippedOutsideLocationCount);
    }

    [TestMethod]
    public async Task AssignCourseAsync_SendsAssignedEmail_ByDefault()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IEmailService> emailServiceMock = new();
        CourseAssignmentService service = GetService(options, emailServiceMock);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);

        Result<CourseAssignment> result = await service.AssignCourseAsync(course.Id, user.Id, "manager-1", null, AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        emailServiceMock.Verify(e => e.SendTrainingAssignedEmailAsync(
            It.Is<UserProfile>(u => u.Id == user.Id), course.Name, It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()),
            Times.Once);
    }

    [TestMethod]
    public async Task AssignCourseAsync_SuppressesAssignedEmail_WhenSendAssignedEmailIsFalse()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IEmailService> emailServiceMock = new();
        CourseAssignmentService service = GetService(options, emailServiceMock);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);

        Result<CourseAssignment> result = await service.AssignCourseAsync(course.Id, user.Id, "manager-1", null, AdminScope, sendAssignedEmail: false);

        Assert.IsTrue(result.Success, result.Error);
        emailServiceMock.Verify(e => e.SendTrainingAssignedEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()),
            Times.Never);
    }

    [TestMethod]
    public async Task AssignCourseToUsersAsync_SendsOneAssignedEmailPerNewAssignment()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IEmailService> emailServiceMock = new();
        CourseAssignmentService service = GetService(options, emailServiceMock);
        Course course = await SeedCourseAsync(options);
        UserProfile userA = await SeedUserAsync(options, "user-a");
        UserProfile userB = await SeedUserAsync(options, "user-b");

        Result<BulkAssignResult> result = await service.AssignCourseToUsersAsync(
            course.Id, [userA.Id, userB.Id], "manager-1", null, AdminScope);

        Assert.IsTrue(result.Success, result.Error);
        emailServiceMock.Verify(e => e.SendTrainingAssignedEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()),
            Times.Exactly(2));
    }

    [TestMethod]
    public async Task AssignCourseToUsersAsync_SuppressesAssignedEmail_WhenSendAssignedEmailIsFalse()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Mock<IEmailService> emailServiceMock = new();
        CourseAssignmentService service = GetService(options, emailServiceMock);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);

        Result<BulkAssignResult> result = await service.AssignCourseToUsersAsync(
            course.Id, [user.Id], null, null, AdminScope, sendAssignedEmail: false);

        Assert.IsTrue(result.Success, result.Error);
        emailServiceMock.Verify(e => e.SendTrainingAssignedEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>()),
            Times.Never);
    }

    [TestMethod]
    public async Task GetAssignmentsDueSoonAsync_ReturnsAssignment_WhenDueDateIsWithinThreshold()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id, dueDate: DateTime.UtcNow.AddDays(3));

        Result<List<CourseAssignment>> result = await service.GetAssignmentsDueSoonAsync(7);

        Assert.IsTrue(result.Success, result.Error);
        Assert.HasCount(1, result.Data!);
        Assert.AreEqual(assignment.Id, result.Data![0].Id);
    }

    [TestMethod]
    public async Task GetAssignmentsDueSoonAsync_ExcludesAssignment_WhenDueDateIsBeyondThreshold()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        await SeedAssignmentAsync(options, course.Id, user.Id, dueDate: DateTime.UtcNow.AddDays(14));

        Result<List<CourseAssignment>> result = await service.GetAssignmentsDueSoonAsync(7);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsEmpty(result.Data!);
    }

    [TestMethod]
    public async Task GetAssignmentsDueSoonAsync_ExcludesAssignment_WhenAlreadyReminded()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        await SeedAssignmentAsync(options, course.Id, user.Id, dueDate: DateTime.UtcNow.AddDays(3), dueSoonReminderSentDate: DateTime.UtcNow);

        Result<List<CourseAssignment>> result = await service.GetAssignmentsDueSoonAsync(7);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsEmpty(result.Data!);
    }

    [TestMethod]
    public async Task GetAssignmentsDueSoonAsync_ExcludesAssignment_WhenAlreadyCompleted()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        await SeedAssignmentAsync(options, course.Id, user.Id, dueDate: DateTime.UtcNow.AddDays(3), completedDate: DateTime.UtcNow);

        Result<List<CourseAssignment>> result = await service.GetAssignmentsDueSoonAsync(7);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsEmpty(result.Data!);
    }

    [TestMethod]
    public async Task GetAssignmentsDueSoonAsync_ExcludesAssignment_WhenAlreadyOverdue()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        await SeedAssignmentAsync(options, course.Id, user.Id, dueDate: DateTime.UtcNow.AddDays(-1));

        Result<List<CourseAssignment>> result = await service.GetAssignmentsDueSoonAsync(7);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsEmpty(result.Data!);
    }

    [TestMethod]
    public async Task MarkDueSoonReminderSentAsync_StampsReminderDate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(options, course.Id, user.Id, dueDate: DateTime.UtcNow.AddDays(3));

        Result<bool> result = await service.MarkDueSoonReminderSentAsync(assignment.Id);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext verifyCtx = new(options);
        CourseAssignment dbAssignment = await verifyCtx.CourseAssignments.SingleAsync(x => x.Id == assignment.Id);
        Assert.IsNotNull(dbAssignment.DueSoonReminderSentDate);
    }

    [TestMethod]
    public async Task MarkDueSoonReminderSentAsync_FailsWhenAssignmentNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);

        Result<bool> result = await service.MarkDueSoonReminderSentAsync(Guid.NewGuid());

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task UpdateAssignmentDueDateAsync_ResetsDueSoonReminderSentDate_WhenDueDateChanges()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);
        CourseAssignment assignment = await SeedAssignmentAsync(
            options, course.Id, user.Id, dueDate: DateTime.UtcNow.AddDays(3), dueSoonReminderSentDate: DateTime.UtcNow);

        Result<CourseAssignment> result = await service.UpdateAssignmentDueDateAsync(assignment.Id, DateTime.UtcNow.AddDays(30));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(result.Data!.DueSoonReminderSentDate);
    }

    [TestMethod]
    public async Task UpdateAssignmentDueDateAsync_KeepsDueSoonReminderSentDate_WhenDueDateIsUnchanged()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CourseAssignmentService service = GetService(options);
        Course course = await SeedCourseAsync(options);
        UserProfile user = await SeedUserAsync(options);

        // Seed with an already-normalized DueDate (same shape UpdateAssignmentDueDateAsync
        // produces via NormalizeDueDate) so re-submitting the same calendar day is a true no-op.
        DateTime rawDueDate = DateTime.UtcNow.AddDays(3);
        DateTime normalizedDueDate = DateTime.SpecifyKind(rawDueDate.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc);
        CourseAssignment assignment = await SeedAssignmentAsync(
            options, course.Id, user.Id, dueDate: normalizedDueDate, dueSoonReminderSentDate: DateTime.UtcNow);

        Result<CourseAssignment> result = await service.UpdateAssignmentDueDateAsync(assignment.Id, rawDueDate);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Data!.DueSoonReminderSentDate);
    }
}
