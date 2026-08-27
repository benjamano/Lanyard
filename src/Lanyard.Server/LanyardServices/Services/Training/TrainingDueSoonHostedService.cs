using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.Branding;
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
/// Sweeps for active, incomplete course assignments whose DueDate falls within the
/// due-soon window and emails the affected user once. Idempotent via
/// CourseAssignment.DueSoonReminderSentDate, which the sweep stamps after a successful
/// send - unlike recurrence, a due date approaching doesn't change assignment state on
/// its own, so an explicit flag (reset by CourseAssignmentService.UpdateAssignmentDueDateAsync
/// when the due date changes) is needed to avoid re-sending on every sweep. Same
/// periodic-sweep shape as CourseRecurrenceHostedService.
/// </summary>
public class TrainingDueSoonHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<TrainingDueSoonHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromHours(12);
    private const int _dueSoonDaysThreshold = 7;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<TrainingDueSoonHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("TrainingDueSoonHostedService started");

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSweepAsync(stoppingToken);
                await Task.Delay(_sweepInterval, stoppingToken);
            }

            _logger.LogInformation("TrainingDueSoonHostedService stopped");
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrainingDueSoonHostedService terminated with an unhandled exception");
        }
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ICourseAssignmentService assignmentService = scope.ServiceProvider.GetRequiredService<ICourseAssignmentService>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IDbContextFactory<ApplicationDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        EmailOptions emailOptions = scope.ServiceProvider.GetRequiredService<IOptions<EmailOptions>>().Value;
        ITrainingBrandingResolver brandingResolver = scope.ServiceProvider.GetRequiredService<ITrainingBrandingResolver>();

        Result<List<CourseAssignment>> dueResult = await assignmentService.GetAssignmentsDueSoonAsync(_dueSoonDaysThreshold);

        if (!dueResult.IsSuccess || dueResult.Data is null)
        {
            _logger.LogWarning("Failed to load assignments due soon: {Error}", dueResult.Error);
            return;
        }

        if (dueResult.Data.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Processing {Count} training due-soon reminders", dueResult.Data.Count);

        await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(stoppingToken);

        foreach (CourseAssignment assignment in dueResult.Data)
        {
            try
            {
                UserProfile? user = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assignment.UserId, stoppingToken);

                if (user is null)
                {
                    continue;
                }

                string trainingUrl = $"{emailOptions.PublicBaseUrl.TrimEnd('/')}/training/{assignment.Id}";

                TrainingBranding branding = await brandingResolver.ResolveAsync(
                    assignment.UserId, assignment.LocationId, assignment.Course?.LocationId);

                string accentColorHex = branding.AccentColorHex;

                // See MainLayout.ApplyBrandingAsync - the endpoint is cache-keyed by URL, so a
                // logo replacement needs a new URL to guarantee a fresh fetch.
                string? logoUrl = branding is { CompanyId: int companyId, LogoFileId: Guid logoFileId }
                    ? $"{emailOptions.PublicBaseUrl.TrimEnd('/')}/api/companies/{companyId}/logo?v={logoFileId:N}"
                    : null;

                Result<bool> emailResult = await emailService.SendTrainingDueSoonEmailAsync(
                    user, assignment.Course?.Name ?? "your training course", assignment.DueDate!.Value, trainingUrl, logoUrl, accentColorHex);

                if (!emailResult.IsSuccess)
                {
                    _logger.LogWarning("Failed to send due-soon reminder email for assignment {AssignmentId}: {Error}",
                        assignment.Id, emailResult.Error);
                    continue;
                }

                Result<bool> markResult = await assignmentService.MarkDueSoonReminderSentAsync(assignment.Id);

                if (!markResult.IsSuccess)
                {
                    _logger.LogWarning("Failed to mark due-soon reminder sent for assignment {AssignmentId}: {Error}",
                        assignment.Id, markResult.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing due-soon reminder for assignment {AssignmentId}", assignment.Id);
            }
        }
    }
}
