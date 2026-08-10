using Lanyard.Application.Services.Email;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services.Training;

/// <summary>
/// Sweeps for completed course assignments whose course has an opted-in
/// RecurrenceMonths and whose retake window has expired, starts a fresh cycle
/// for each, and emails the affected user. Idempotent by construction: once a
/// new cycle exists it becomes the latest AssignedDate for that (course, user)
/// pair and has no CompletedDate, so the due-ness query in
/// GetAssignmentsDueForRecurrenceAsync naturally stops selecting it - no
/// separate "processed" flag is needed. Same periodic-sweep shape as
/// SongAnalysisHostedService.
/// </summary>
public class CourseRecurrenceHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<CourseRecurrenceHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromHours(12);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<CourseRecurrenceHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("CourseRecurrenceHostedService started");

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSweepAsync(stoppingToken);
                await Task.Delay(_sweepInterval, stoppingToken);
            }

            _logger.LogInformation("CourseRecurrenceHostedService stopped");
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CourseRecurrenceHostedService terminated with an unhandled exception");
        }
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ICourseAssignmentService assignmentService = scope.ServiceProvider.GetRequiredService<ICourseAssignmentService>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IDbContextFactory<ApplicationDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        EmailOptions emailOptions = scope.ServiceProvider.GetRequiredService<IOptions<EmailOptions>>().Value;

        Result<List<CourseAssignment>> dueResult = await assignmentService.GetAssignmentsDueForRecurrenceAsync();

        if (!dueResult.IsSuccess || dueResult.Data is null)
        {
            _logger.LogWarning("Failed to load assignments due for recurrence: {Error}", dueResult.Error);
            return;
        }

        if (dueResult.Data.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Processing {Count} course recurrence cycles", dueResult.Data.Count);

        await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(stoppingToken);

        foreach (CourseAssignment previous in dueResult.Data)
        {
            try
            {
                Result<CourseAssignment> newCycleResult = await assignmentService.ProcessRecurrenceCycleAsync(previous.Id);

                if (!newCycleResult.IsSuccess || newCycleResult.Data is null)
                {
                    _logger.LogInformation("Skipped recurrence cycle for assignment {AssignmentId}: {Error}",
                        previous.Id, newCycleResult.Error);
                    continue;
                }

                UserProfile? user = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == previous.UserId, stoppingToken);

                if (user is null)
                {
                    continue;
                }

                string trainingUrl = $"{emailOptions.PublicBaseUrl.TrimEnd('/')}/training/{newCycleResult.Data.Id}";

                Result<bool> emailResult = await emailService.SendCourseRecurrenceReminderEmailAsync(
                    user, previous.Course?.Name ?? "your training course", trainingUrl);

                if (!emailResult.IsSuccess)
                {
                    _logger.LogWarning("Failed to send recurrence reminder email for assignment {AssignmentId}: {Error}",
                        newCycleResult.Data.Id, emailResult.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing recurrence cycle for assignment {AssignmentId}", previous.Id);
            }
        }
    }
}
