using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Training;

public class CourseService(IDbContextFactory<ApplicationDbContext> factory) : ICourseService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<List<Course>>> GetCoursesAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<Course> courses = await ctx.Courses
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive)
                .OrderBy(x => x.Name)
                .ToListAsync();

            return Result<List<Course>>.Ok(courses);
        }
        catch (Exception ex)
        {
            return Result<List<Course>>.Fail($"Failed to retrieve courses: {ex.Message}");
        }
    }

    public async Task<Result<Course>> GetCourseAsync(Guid courseId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Course? course = await ctx.Courses
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Sections.Where(s => s.IsActive))
                .Include(x => x.Questions.Where(q => q.IsActive))
                    .ThenInclude(x => x.Options.Where(o => o.IsActive))
                .FirstOrDefaultAsync(x => x.Id == courseId);

            if (course is null)
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

    public async Task<Result<Course>> SaveCourseAsync(Course course)
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

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Course? existingCourse = course.Id == Guid.Empty
                ? null
                : await ctx.Courses.FirstOrDefaultAsync(x => x.Id == course.Id);

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
                    IsActive = true
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
                targetCourse.IsActive = true;
            }

            await ctx.SaveChangesAsync();

            return Result<Course>.Ok(targetCourse);
        }
        catch (Exception ex)
        {
            return Result<Course>.Fail($"Failed to save course: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteCourseAsync(Guid courseId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Course? course = await ctx.Courses.FirstOrDefaultAsync(x => x.Id == courseId);

            if (course is null)
            {
                return Result<bool>.Fail("Course not found.");
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

    public Task<Result<CourseSection>> SaveSectionAsync(CourseSection section) => throw new NotImplementedException();
    public Task<Result<bool>> DeleteSectionAsync(Guid sectionId) => throw new NotImplementedException();
    public Task<Result<CourseQuestion>> SaveQuestionAsync(CourseQuestion question) => throw new NotImplementedException();
    public Task<Result<bool>> DeleteQuestionAsync(Guid questionId) => throw new NotImplementedException();
}
