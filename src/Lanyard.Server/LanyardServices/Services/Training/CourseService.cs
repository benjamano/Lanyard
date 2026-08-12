using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Training;

public class CourseService(IDbContextFactory<ApplicationDbContext> factory) : ICourseService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<List<Course>>> GetCoursesAsync(LocationScope scope)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<Course> query = ctx.Courses
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .Where(x => x.IsActive);

            if (!scope.IsAdmin)
            {
                query = query.Where(x =>
                    x.LocationId == scope.LocationId ||
                    (x.IsShared && x.Location!.CompanyId == scope.CompanyId));
            }

            List<Course> courses = await query.OrderBy(x => x.Name).ToListAsync();

            return Result<List<Course>>.Ok(courses);
        }
        catch (Exception ex)
        {
            return Result<List<Course>>.Fail($"Failed to retrieve courses: {ex.Message}");
        }
    }

    public async Task<Result<Course>> GetCourseAsync(Guid courseId, LocationScope scope)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Course? course = await ctx.Courses
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .Include(x => x.Sections.Where(s => s.IsActive))
                .Include(x => x.Questions.Where(q => q.IsActive))
                    .ThenInclude(x => x.Options.Where(o => o.IsActive))
                .FirstOrDefaultAsync(x => x.Id == courseId && x.IsActive);

            if (course is null || (!scope.IsAdmin && course.LocationId != scope.LocationId
                && !(course.IsShared && course.Location is not null && course.Location.CompanyId == scope.CompanyId)))
            {
                return Result<Course>.Fail("Course not found.");
            }

            course.Sections = [.. course.Sections.OrderBy(x => x.SortOrder)];
            course.Questions = [.. course.Questions.OrderBy(x => x.SortOrder)];

            foreach (CourseQuestion question in course.Questions)
            {
                question.Options = [.. question.Options.OrderBy(x => x.SortOrder)];
            }

            return Result<Course>.Ok(course);
        }
        catch (Exception ex)
        {
            return Result<Course>.Fail($"Failed to retrieve course: {ex.Message}");
        }
    }

    public async Task<Result<Course>> SaveCourseAsync(Course course, LocationScope scope)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(course.Name))
            {
                return Result<Course>.Fail("Course name is required.");
            }

            if (course.PassMarkPercent < 1 || course.PassMarkPercent > 100)
            {
                return Result<Course>.Fail("Pass mark must be between 1 and 100.");
            }

            if (course.RecurrenceMonths.HasValue && course.RecurrenceMonths < 1)
            {
                return Result<Course>.Fail("Recurrence interval must be at least 1 month.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Course? existingCourse = course.Id == Guid.Empty
                ? null
                : await ctx.Courses.FirstOrDefaultAsync(x => x.Id == course.Id);

            if (existingCourse is not null && !scope.IsAdmin && existingCourse.LocationId != scope.LocationId)
            {
                return Result<Course>.Fail("You do not have access to this course.");
            }

            if (existingCourse is null && !scope.IsAdmin && scope.LocationId is null)
            {
                return Result<Course>.Fail("You do not have access to create courses.");
            }

            Course targetCourse;

            if (existingCourse is null)
            {
                targetCourse = new Course
                {
                    Id = course.Id == Guid.Empty ? Guid.NewGuid() : course.Id,
                    Name = course.Name.Trim(),
                    Description = course.Description?.Trim(),
                    PassMarkPercent = course.PassMarkPercent,
                    AutoAssignOnUserCreation = course.AutoAssignOnUserCreation,
                    RecurrenceMonths = course.RecurrenceMonths,
                    IsActive = true,
                    LocationId = scope.IsAdmin ? course.LocationId : scope.LocationId,
                    IsShared = course.IsShared
                };

                ctx.Courses.Add(targetCourse);
            }
            else
            {
                targetCourse = existingCourse;
                targetCourse.Name = course.Name.Trim();
                targetCourse.Description = course.Description?.Trim();
                targetCourse.PassMarkPercent = course.PassMarkPercent;
                targetCourse.AutoAssignOnUserCreation = course.AutoAssignOnUserCreation;
                targetCourse.RecurrenceMonths = course.RecurrenceMonths;
                targetCourse.IsActive = true;
                targetCourse.IsShared = course.IsShared;

                // Only an Admin may move a course between locations. For a non-Admin the
                // LocationId stays pinned to whatever it was created with - deliberately not
                // reassigned, so a tampered client payload can never re-home someone else's
                // course (the access check above already forced the course into their location).
                if (scope.IsAdmin)
                {
                    targetCourse.LocationId = course.LocationId;
                }
            }

            await ctx.SaveChangesAsync();

            return Result<Course>.Ok(targetCourse);
        }
        catch (Exception ex)
        {
            return Result<Course>.Fail($"Failed to save course: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteCourseAsync(Guid courseId, LocationScope scope)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Course? course = await ctx.Courses.FirstOrDefaultAsync(x => x.Id == courseId);

            if (course is null)
            {
                return Result<bool>.Fail("Course not found.");
            }

            if (!scope.IsAdmin && course.LocationId != scope.LocationId)
            {
                return Result<bool>.Fail("You do not have access to this course.");
            }

            course.IsActive = false;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to delete course: {ex.Message}");
        }
    }

    public async Task<Result<CourseSection>> SaveSectionAsync(CourseSection section)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(section.Title))
            {
                return Result<CourseSection>.Fail("Section title is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool courseExists = await ctx.Courses.AnyAsync(x => x.Id == section.CourseId);

            if (!courseExists)
            {
                return Result<CourseSection>.Fail("Course not found.");
            }

            CourseSection? existingSection = section.Id == Guid.Empty
                ? null
                : await ctx.CourseSections.FirstOrDefaultAsync(x => x.Id == section.Id);

            CourseSection targetSection;

            if (existingSection is null)
            {
                int nextSortOrder = await ctx.CourseSections.CountAsync(x => x.CourseId == section.CourseId && x.IsActive);

                targetSection = new CourseSection
                {
                    Id = section.Id == Guid.Empty ? Guid.NewGuid() : section.Id,
                    CourseId = section.CourseId,
                    Title = section.Title.Trim(),
                    BodyHtml = section.BodyHtml,
                    SortOrder = nextSortOrder,
                    IsActive = true
                };

                ctx.CourseSections.Add(targetSection);
            }
            else
            {
                targetSection = existingSection;
                targetSection.Title = section.Title.Trim();
                targetSection.BodyHtml = section.BodyHtml;
                targetSection.IsActive = true;
            }

            await ctx.SaveChangesAsync();

            return Result<CourseSection>.Ok(targetSection);
        }
        catch (Exception ex)
        {
            return Result<CourseSection>.Fail($"Failed to save section: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteSectionAsync(Guid sectionId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CourseSection? section = await ctx.CourseSections.FirstOrDefaultAsync(x => x.Id == sectionId);

            if (section is null)
            {
                return Result<bool>.Fail("Section not found.");
            }

            section.IsActive = false;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to delete section: {ex.Message}");
        }
    }
    public async Task<Result<bool>> ReorderSectionsAsync(Guid courseId, List<Guid> orderedSectionIds)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<CourseSection> sections = await ctx.CourseSections
                .Where(x => x.CourseId == courseId && x.IsActive)
                .ToListAsync();

            for (int i = 0; i < orderedSectionIds.Count; i++)
            {
                CourseSection? section = sections.FirstOrDefault(x => x.Id == orderedSectionIds[i]);

                if (section is not null)
                {
                    section.SortOrder = i;
                }
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to reorder sections: {ex.Message}");
        }
    }

    public async Task<Result<CourseQuestion>> SaveQuestionAsync(CourseQuestion question)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(question.QuestionText))
            {
                return Result<CourseQuestion>.Fail("Question text is required.");
            }

            List<CourseQuestionOption> incomingOptions = question.Options
                .Where(x => !string.IsNullOrWhiteSpace(x.OptionText))
                .ToList();

            if (incomingOptions.Count < 2)
            {
                return Result<CourseQuestion>.Fail("A question needs at least two answer options.");
            }

            if (incomingOptions.Count(x => x.IsCorrect) != 1)
            {
                return Result<CourseQuestion>.Fail("Exactly one option must be marked as the correct answer.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool courseExists = await ctx.Courses.AnyAsync(x => x.Id == question.CourseId);

            if (!courseExists)
            {
                return Result<CourseQuestion>.Fail("Course not found.");
            }

            CourseQuestion? existingQuestion = question.Id == Guid.Empty
                ? null
                : await ctx.CourseQuestions
                    .Include(x => x.Options)
                    .FirstOrDefaultAsync(x => x.Id == question.Id);

            CourseQuestion targetQuestion;

            if (existingQuestion is null)
            {
                int nextSortOrder = await ctx.CourseQuestions.CountAsync(x => x.CourseId == question.CourseId && x.IsActive);

                targetQuestion = new CourseQuestion
                {
                    Id = question.Id == Guid.Empty ? Guid.NewGuid() : question.Id,
                    CourseId = question.CourseId,
                    QuestionText = question.QuestionText.Trim(),
                    SortOrder = nextSortOrder,
                    IsActive = true,
                    Options = []
                };

                ctx.CourseQuestions.Add(targetQuestion);
            }
            else
            {
                targetQuestion = existingQuestion;
                targetQuestion.QuestionText = question.QuestionText.Trim();
                targetQuestion.IsActive = true;
            }

            Dictionary<Guid, CourseQuestionOption> existingOptions = targetQuestion.Options.ToDictionary(x => x.Id);
            HashSet<Guid> seenOptionIds = [];

            foreach (CourseQuestionOption incomingOption in incomingOptions)
            {
                Guid optionId = incomingOption.Id == Guid.Empty ? Guid.NewGuid() : incomingOption.Id;

                if (existingOptions.TryGetValue(optionId, out CourseQuestionOption? existingOption))
                {
                    existingOption.OptionText = incomingOption.OptionText.Trim();
                    existingOption.IsCorrect = incomingOption.IsCorrect;
                    existingOption.SortOrder = incomingOption.SortOrder;
                    existingOption.IsActive = true;
                }
                else
                {
                    CourseQuestionOption newOption = new CourseQuestionOption
                    {
                        Id = optionId,
                        QuestionId = targetQuestion.Id,
                        OptionText = incomingOption.OptionText.Trim(),
                        IsCorrect = incomingOption.IsCorrect,
                        SortOrder = incomingOption.SortOrder,
                        IsActive = true
                    };
                    ctx.CourseQuestionOptions.Add(newOption);
                    targetQuestion.Options.Add(newOption);
                }

                seenOptionIds.Add(optionId);
            }

            foreach (CourseQuestionOption optionToRemove in targetQuestion.Options.Where(x => x.IsActive && !seenOptionIds.Contains(x.Id)))
            {
                optionToRemove.IsActive = false;
            }

            await ctx.SaveChangesAsync();

            CourseQuestion? savedQuestion = await ctx.CourseQuestions
                .AsNoTracking()
                .Include(x => x.Options.Where(o => o.IsActive))
                .FirstOrDefaultAsync(x => x.Id == targetQuestion.Id);

            if (savedQuestion is null)
            {
                return Result<CourseQuestion>.Fail("Question saved but could not be reloaded.");
            }

            savedQuestion.Options = [.. savedQuestion.Options.OrderBy(x => x.SortOrder)];

            return Result<CourseQuestion>.Ok(savedQuestion);
        }
        catch (Exception ex)
        {
            return Result<CourseQuestion>.Fail($"Failed to save question: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteQuestionAsync(Guid questionId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CourseQuestion? question = await ctx.CourseQuestions.FirstOrDefaultAsync(x => x.Id == questionId);

            if (question is null)
            {
                return Result<bool>.Fail("Question not found.");
            }

            question.IsActive = false;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to delete question: {ex.Message}");
        }
    }

    public async Task<Result<bool>> ReorderQuestionsAsync(Guid courseId, List<Guid> orderedQuestionIds)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<CourseQuestion> questions = await ctx.CourseQuestions
                .Where(x => x.CourseId == courseId && x.IsActive)
                .ToListAsync();

            for (int i = 0; i < orderedQuestionIds.Count; i++)
            {
                CourseQuestion? question = questions.FirstOrDefault(x => x.Id == orderedQuestionIds[i]);

                if (question is not null)
                {
                    question.SortOrder = i;
                }
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to reorder questions: {ex.Message}");
        }
    }
}
