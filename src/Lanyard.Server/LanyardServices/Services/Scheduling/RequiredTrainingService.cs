using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.DTO.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class RequiredTrainingService(
    IDbContextFactory<ApplicationDbContext> factory,
    ICourseAssignmentService courseAssignmentService,
    ILogger<RequiredTrainingService> logger) : IRequiredTrainingService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ICourseAssignmentService _courseAssignmentService = courseAssignmentService;
    private readonly ILogger<RequiredTrainingService> _logger = logger;

    public async Task<Result<RequiredTrainingAssignResult>> AssignMissingAsync(
        LocationScope scope,
        string actingUserId,
        IReadOnlyCollection<(string UserId, Guid PositionId)> holdings,
        IReadOnlyCollection<Guid>? onlyCourseIds = null)
    {
        try
        {
            if (holdings.Count == 0)
            {
                return Result<RequiredTrainingAssignResult>.Ok(RequiredTrainingAssignResult.Empty);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<string> userIds = holdings.Select(x => x.UserId).Distinct().ToList();

            // Active courses only, and nothing for a company with Training switched off.
            ShiftClaimRules.TrainingSnapshot training = await ShiftClaimRules.LoadTrainingAsync(ctx, userIds, holdings.Select(x => x.PositionId).ToList());

            if (!training.Requirements.Any())
            {
                return Result<RequiredTrainingAssignResult>.Ok(RequiredTrainingAssignResult.Empty);
            }

            var memberships = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => userIds.Contains(x.UserId))
                .Select(x => new { x.UserId, x.LocationId })
                .ToListAsync();

            DateTime now = DateTime.UtcNow;
            Dictionary<Guid, (string Name, HashSet<string> Users)> wanted = [];
            int alreadyOnIt = 0;

            foreach (IGrouping<string, (string UserId, Guid PositionId)> person in holdings.GroupBy(x => x.UserId))
            {
                Dictionary<Guid, ShiftClaimRules.TrainingRecord> record = training.ByUser.GetValueOrDefault(person.Key) ?? [];

                List<ShiftClaimRules.RequiredCourse> courses = person
                    .SelectMany(x => training.Requirements[x.PositionId])
                    .Where(x => onlyCourseIds is null || onlyCourseIds.Contains(x.CourseId))
                    .DistinctBy(x => x.CourseId)
                    .ToList();

                // Not current today at a location they work at - a course from another site
                // isn't theirs to take.
                List<int?> workAt = memberships.Where(x => x.UserId == person.Key).Select(x => (int?)x.LocationId).ToList();

                HashSet<Guid> missing = workAt
                    .SelectMany(at => ShiftClaimRules.CourseGaps(courses, record, at, now))
                    .Select(x => x.CourseId)
                    .ToHashSet();

                foreach (ShiftClaimRules.RequiredCourse course in courses.Where(x => missing.Contains(x.CourseId)))
                {
                    // Already given it (a retake the recurrence sweep started, or one assigned by hand).
                    if (record.GetValueOrDefault(course.CourseId)?.OpenAssignmentId is not null)
                    {
                        alreadyOnIt++;
                        continue;
                    }

                    if (!wanted.TryGetValue(course.CourseId, out (string Name, HashSet<string> Users) entry))
                    {
                        entry = (course.Name, []);
                        wanted[course.CourseId] = entry;
                    }

                    entry.Users.Add(person.Key);
                }
            }

            List<AssignedCourse> assigned = [];
            int couldNotAssign = 0;

            foreach ((Guid courseId, (string name, HashSet<string> users)) in wanted)
            {
                Result<BulkAssignResult> result = await _courseAssignmentService.AssignCourseToUsersAsync(
                    courseId, users.ToList(), actingUserId, null, scope, sendAssignedEmail: true);

                if (!result.IsSuccess || result.Data is null)
                {
                    // Most likely another location's course a Manager can't see.
                    _logger.LogWarning("Couldn't assign required course {CourseId} to {Count} people: {Error}", courseId, users.Count, result.Error);
                    couldNotAssign += users.Count;
                    continue;
                }

                couldNotAssign += result.Data.SkippedOutsideLocationCount;
                alreadyOnIt += result.Data.SkippedDuplicateCount;

                if (result.Data.AssignedCount > 0)
                {
                    assigned.Add(new AssignedCourse(courseId, name, result.Data.AssignedCount));
                }
            }

            if (assigned.Count > 0)
            {
                _logger.LogInformation("{UserId} assigned required training: {Assignments} assignments across {Courses} courses",
                    actingUserId, assigned.Sum(x => x.People), assigned.Count);
            }

            return Result<RequiredTrainingAssignResult>.Ok(new RequiredTrainingAssignResult(assigned, alreadyOnIt, couldNotAssign));
        }
        catch (Exception ex)
        {
            return Result<RequiredTrainingAssignResult>.Fail($"Failed to assign the required training: {ex.Message}");
        }
    }
}
