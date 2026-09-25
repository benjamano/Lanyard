using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

/// <summary>
/// Every 15 minutes, finds published shifts that have come within their company's reminder lead
/// time and queues one reminder email per shift. Same periodic-sweep shape as
/// StaffDocumentExpiryReminderHostedService, and the same claim-before-send rule: the shift is
/// marked as reminded before the email is queued, so two overlapping sweeps can never both send.
/// A shift moved to a new time is reminded again for the new time.
/// </summary>
public class ShiftReminderHostedService(
    IServiceScopeFactory scopeFactory,
    INotificationDispatcher notifications,
    TimeProvider timeProvider,
    ILogger<ShiftReminderHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly INotificationDispatcher _notifications = notifications;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ShiftReminderHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ShiftReminderHostedService started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using (IServiceScope scope = _scopeFactory.CreateScope())
                {
                    await RunSweepAsync(scope.ServiceProvider.GetRequiredService<IRotaService>(), stoppingToken);
                }

                await Task.Delay(SweepInterval, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ShiftReminderHostedService terminated with an unhandled exception");
        }

        _logger.LogInformation("ShiftReminderHostedService stopped");
    }

    // Public so tests can run one sweep without the timer.
    public async Task<int> RunSweepAsync(IRotaService rotaService, CancellationToken cancellationToken = default)
    {
        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        Result<List<ShiftReminderDue>> dueResult = await rotaService.GetShiftsDueForReminderAsync(nowUtc);

        if (!dueResult.IsSuccess || dueResult.Data is null)
        {
            _logger.LogWarning("Couldn't load shifts due a reminder: {Error}", dueResult.Error);
            return 0;
        }

        int queued = 0;

        foreach (ShiftReminderDue due in dueResult.Data)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Result<bool> claim = await rotaService.ClaimShiftReminderAsync(due.Shift.Id, due.Shift.StartUtc);

            if (!claim.IsSuccess)
            {
                _logger.LogInformation("Skipping the reminder for shift {ShiftId}: {Reason}", due.Shift.Id, claim.Error);
                continue;
            }

            if (!due.SendEmail)
            {
                continue;
            }

            _notifications.Enqueue([due.Shift.UserId!], NotificationTopic.ShiftReminder, new ShiftReminderPayload(
                due.Shift.LocationId,
                due.Shift.Location?.Name ?? "work",
                RotaService.ToEmailLine(due.Shift)));

            queued++;
        }

        if (queued > 0)
        {
            _logger.LogInformation("Queued {Count} shift reminders", queued);
        }

        return queued;
    }
}
