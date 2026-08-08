using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Training;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Training;

public class CourseAssignmentService(IDbContextFactory<ApplicationDbContext> factory) : ICourseAssignmentService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<CourseAssignment>> AssignCourseAsync(Guid courseId, string userId, string assignedByUserId, DateTime? dueDate)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool courseExists = await ctx.Courses.AnyAsync(x => x.Id == courseId && x.IsActive);

            if (!courseExists)
            {
                return Result<CourseAssignment>.Fail("Course not found.");
            }

            bool userExists = await ctx.Users.AnyAsync(x => x.Id == userId);

            if (!userExists)
            {
                return Result<CourseAssignment>.Fail("User not found.");
            }

            bool alreadyAssigned = await ctx.CourseAssignments.AnyAsync(x => x.CourseId == courseId && x.UserId == userId && x.IsActive);

            if (alreadyAssigned)
            {
                return Result<CourseAssignment>.Fail("This course is already assigned to that user.");
            }

            DateTime? normalizedDueDate = NormalizeDueDate(dueDate);

            CourseAssignment assignment = new()
            {
                Id = Guid.NewGuid(),
                CourseId = courseId,
                UserId = userId,
                AssignedByUserId = assignedByUserId,
                AssignedDate = DateTime.UtcNow,
                DueDate = normalizedDueDate,
                IsActive = true
            };

            ctx.CourseAssignments.Add(assignment);
            await ctx.SaveChangesAsync();

            return Result<CourseAssignment>.Ok(assignment);
        }
        catch (Exception ex)
        {
            return Result<CourseAssignment>.Fail($"Failed to assign course: {ex.Message}");
        }
    }

    public async Task<Result<List<CourseAssignment>>> GetAssignmentsForUserAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<CourseAssignment> assignments = await ctx.CourseAssignments
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == userId && x.IsActive && (x.Course!.IsActive || x.CompletedDate != null))
                .Include(x => x.Course)
                .Include(x => x.Attempts)
                .OrderBy(x => x.AssignedDate)
                .ToListAsync();

            return Result<List<CourseAssignment>>.Ok(assignments);
        }
        catch (Exception ex)
        {
            return Result<List<CourseAssignment>>.Fail($"Failed to retrieve assignments: {ex.Message}");
        }
    }

    public async Task<Result<CourseAssignment>> GetAssignmentAsync(Guid assignmentId, string requestingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CourseAssignment? assignment = await ctx.CourseAssignments
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Course!).ThenInclude(c => c.Sections.Where(s => s.IsActive))
                .Include(x => x.Course!).ThenInclude(c => c.Questions.Where(q => q.IsActive)).ThenInclude(q => q.Options.Where(o => o.IsActive))
                .Include(x => x.Attempts).ThenInclude(a => a.Answers)
                .FirstOrDefaultAsync(x => x.Id == assignmentId && x.IsActive && (x.Course!.IsActive || x.CompletedDate != null));

            if (assignment is null)
            {
                return Result<CourseAssignment>.Fail("Assignment not found.");
            }

            if (assignment.UserId != requestingUserId)
            {
                return Result<CourseAssignment>.Fail("You do not have access to this assignment.");
            }

            assignment.Course!.Sections = [.. assignment.Course.Sections.OrderBy(x => x.SortOrder)];
            assignment.Course.Questions = [.. assignment.Course.Questions.OrderBy(x => x.SortOrder)];

            foreach (CourseQuestion question in assignment.Course.Questions)
            {
                question.Options = [.. question.Options.OrderBy(x => x.SortOrder)];
            }

            return Result<CourseAssignment>.Ok(assignment);
        }
        catch (Exception ex)
        {
            return Result<CourseAssignment>.Fail($"Failed to retrieve assignment: {ex.Message}");
        }
    }

    public async Task<Result<CourseAssignment>> StartAssignmentAsync(Guid assignmentId, string requestingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CourseAssignment? assignment = await ctx.CourseAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId && x.IsActive);

            if (assignment is null)
            {
                return Result<CourseAssignment>.Fail("Assignment not found.");
            }

            if (assignment.UserId != requestingUserId)
            {
                return Result<CourseAssignment>.Fail("You do not have access to this assignment.");
            }

            if (assignment.StartedDate is null)
            {
                assignment.StartedDate = DateTime.UtcNow;
                await ctx.SaveChangesAsync();
            }

            return Result<CourseAssignment>.Ok(assignment);
        }
        catch (Exception ex)
        {
            return Result<CourseAssignment>.Fail($"Failed to start assignment: {ex.Message}");
        }
    }

    public async Task<Result<QuizGradeResult>> SubmitQuizAttemptAsync(Guid assignmentId, string requestingUserId, Dictionary<Guid, Guid> answers)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CourseAssignment? assignment = await ctx.CourseAssignments
                .Include(x => x.Course)
                .Include(x => x.Attempts)
                .FirstOrDefaultAsync(x => x.Id == assignmentId && x.IsActive);

            if (assignment is null)
            {
                return Result<QuizGradeResult>.Fail("Assignment not found.");
            }

            if (assignment.UserId != requestingUserId)
            {
                return Result<QuizGradeResult>.Fail("You do not have access to this assignment.");
            }

            List<CourseQuestion> questions = await ctx.CourseQuestions
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Options)
                .Where(x => x.CourseId == assignment.CourseId && x.IsActive)
                .ToListAsync();

            if (questions.Count == 0)
            {
                return Result<QuizGradeResult>.Fail("This course has no quiz questions.");
            }

            int attemptNumber = assignment.Attempts.Count + 1;

            CourseQuizAttempt attempt = new()
            {
                Id = Guid.NewGuid(),
                AssignmentId = assignment.Id,
                AttemptNumber = attemptNumber,
                SubmittedDate = DateTime.UtcNow
            };

            List<QuizQuestionResult> questionResults = [];
            int correctCount = 0;

            foreach (CourseQuestion question in questions)
            {
                bool wasCorrect = false;

                if (answers.TryGetValue(question.Id, out Guid selectedOptionId))
                {
                    CourseQuestionOption? selectedOption = question.Options.FirstOrDefault(x => x.Id == selectedOptionId && x.IsActive);
                    wasCorrect = selectedOption?.IsCorrect ?? false;

                    CourseQuizAttemptAnswer answerEntity = new()
                    {
                        Id = Guid.NewGuid(),
                        AttemptId = attempt.Id,
                        QuestionId = question.Id,
                        SelectedOptionId = selectedOptionId,
                        WasCorrect = wasCorrect
                    };

                    ctx.CourseQuizAttemptAnswers.Add(answerEntity);
                    attempt.Answers.Add(answerEntity);
                }

                questionResults.Add(new QuizQuestionResult(question.Id, wasCorrect));

                if (wasCorrect)
                {
                    correctCount++;
                }
            }

            int scorePercent = (int)Math.Round(correctCount * 100.0 / questions.Count);
            bool passed = scorePercent >= assignment.Course!.PassMarkPercent;

            attempt.ScorePercent = scorePercent;
            attempt.Passed = passed;

            ctx.CourseQuizAttempts.Add(attempt);

            if (passed && assignment.CompletedDate is null)
            {
                assignment.CompletedDate = DateTime.UtcNow;
            }

            await ctx.SaveChangesAsync();

            return Result<QuizGradeResult>.Ok(new QuizGradeResult(scorePercent, passed, attemptNumber, questionResults));
        }
        catch (Exception ex)
        {
            return Result<QuizGradeResult>.Fail($"Failed to submit quiz attempt: {ex.Message}");
        }
    }

    private static DateTime? NormalizeDueDate(DateTime? dueDate) =>
        dueDate.HasValue ? DateTime.SpecifyKind(dueDate.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc) : null;

    public async Task<Result<List<CourseAssignment>>> GetAssignmentsForCourseAsync(Guid courseId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<CourseAssignment> assignments = await ctx.CourseAssignments
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CourseId == courseId && x.IsActive)
                .Include(x => x.Attempts)
                .OrderBy(x => x.AssignedDate)
                .ToListAsync();

            return Result<List<CourseAssignment>>.Ok(assignments);
        }
        catch (Exception ex)
        {
            return Result<List<CourseAssignment>>.Fail($"Failed to retrieve assignments: {ex.Message}");
        }
    }

    public async Task<Result<BulkAssignResult>> AssignCourseToUsersAsync(Guid courseId, List<string> userIds, string? assignedByUserId, DateTime? dueDate)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool courseExists = await ctx.Courses.AnyAsync(x => x.Id == courseId && x.IsActive);

            if (!courseExists)
            {
                return Result<BulkAssignResult>.Fail("Course not found.");
            }

            DateTime? normalizedDueDate = NormalizeDueDate(dueDate);
            List<string> distinctUserIds = [.. userIds.Distinct()];

            HashSet<string> alreadyAssignedUserIds = (await ctx.CourseAssignments
                .Where(x => x.CourseId == courseId && x.IsActive && distinctUserIds.Contains(x.UserId))
                .Select(x => x.UserId)
                .ToListAsync())
                .ToHashSet();

            int assignedCount = 0;

            // userIds are sourced from GetAllUsersAsync (individual picks) or
            // GetUsersInRoleAsync (role members) by the caller - both already
            // resolve to real Identity users, so no per-user existence check here.
            foreach (string userId in distinctUserIds)
            {
                if (alreadyAssignedUserIds.Contains(userId))
                {
                    continue;
                }

                ctx.CourseAssignments.Add(new CourseAssignment
                {
                    Id = Guid.NewGuid(),
                    CourseId = courseId,
                    UserId = userId,
                    AssignedByUserId = assignedByUserId,
                    AssignedDate = DateTime.UtcNow,
                    DueDate = normalizedDueDate,
                    IsActive = true
                });

                assignedCount++;
            }

            await ctx.SaveChangesAsync();

            return Result<BulkAssignResult>.Ok(new BulkAssignResult(assignedCount, distinctUserIds.Count - assignedCount));
        }
        catch (Exception ex)
        {
            return Result<BulkAssignResult>.Fail($"Failed to bulk assign course: {ex.Message}");
        }
    }

    public async Task<Result<CourseAssignment>> UpdateAssignmentDueDateAsync(Guid assignmentId, DateTime? newDueDate)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CourseAssignment? assignment = await ctx.CourseAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId && x.IsActive);

            if (assignment is null)
            {
                return Result<CourseAssignment>.Fail("Assignment not found.");
            }

            assignment.DueDate = NormalizeDueDate(newDueDate);

            await ctx.SaveChangesAsync();

            return Result<CourseAssignment>.Ok(assignment);
        }
        catch (Exception ex)
        {
            return Result<CourseAssignment>.Fail($"Failed to update due date: {ex.Message}");
        }
    }

    public async Task<Result<bool>> UnassignAsync(Guid assignmentId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CourseAssignment? assignment = await ctx.CourseAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId);

            if (assignment is null)
            {
                return Result<bool>.Fail("Assignment not found.");
            }

            assignment.IsActive = false;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to unassign course: {ex.Message}");
        }
    }

    public async Task<Result<int>> UnassignAllForUserAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<CourseAssignment> assignments = await ctx.CourseAssignments
                .Where(x => x.UserId == userId && x.IsActive)
                .ToListAsync();

            foreach (CourseAssignment assignment in assignments)
            {
                assignment.IsActive = false;
            }

            await ctx.SaveChangesAsync();

            return Result<int>.Ok(assignments.Count);
        }
        catch (Exception ex)
        {
            return Result<int>.Fail($"Failed to unassign courses for user: {ex.Message}");
        }
    }
}
