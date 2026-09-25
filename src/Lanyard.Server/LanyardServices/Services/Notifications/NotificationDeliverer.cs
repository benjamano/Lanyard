using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services.Notifications;

public class NotificationDeliverer(
    IDbContextFactory<ApplicationDbContext> factory,
    IEmailService emailService,
    IOptions<EmailOptions> emailOptions,
    ITrainingBrandingResolver brandingResolver,
    TimeProvider timeProvider,
    ILogger<NotificationDeliverer> logger) : INotificationDeliverer
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IEmailService _emailService = emailService;
    private readonly EmailOptions _emailOptions = emailOptions.Value;
    private readonly ITrainingBrandingResolver _brandingResolver = brandingResolver;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<NotificationDeliverer> _logger = logger;

    public async Task DeliverAsync(NotificationJob job, CancellationToken cancellationToken = default)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            UserProfile? user = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == job.UserId, cancellationToken);

            if (user is null || user.Id == ApplicationDbContext.SystemDeletedUserPlaceholderId)
            {
                return;
            }

            // Branding follows the location the notification is about, with the person's own
            // company as the first choice - the same order training emails use.
            TrainingBranding branding = await _brandingResolver.ResolveAsync(user.Id, job.Payload.LocationId, null);
            string? logoUrl = EmailBranding.LogoUrl(_emailOptions, branding);
            string accent = branding.AccentColorHex;

            Result<bool> result = job.Payload switch
            {
                RotaChangedPayload rota => await _emailService.SendRotaChangedEmailAsync(
                    user, rota.LocationName, rota.Added, rota.Changed, rota.Removed,
                    EmailBranding.Link(_emailOptions, "/rota"), logoUrl, accent),

                ShiftReminderPayload reminder => await _emailService.SendShiftReminderEmailAsync(
                    user, reminder.LocationName, reminder.Shift, DayLabel(reminder.Shift.Date),
                    EmailBranding.Link(_emailOptions, "/rota"), logoUrl, accent),

                TimeOffRequestedPayload requested => await _emailService.SendTimeOffRequestedEmailAsync(
                    user, requested.RequesterName, requested.TypeName, requested.Start, requested.End, requested.Amount, requested.Notes,
                    EmailBranding.Link(_emailOptions, "/manage/rota/time-off"), logoUrl, accent),

                TimeOffDecidedPayload decided => await _emailService.SendTimeOffDecisionEmailAsync(
                    user, decided.TypeName, decided.Start, decided.End, decided.Outcome, decided.Reason, decided.DecidedByName,
                    EmailBranding.Link(_emailOptions, "/rota/time-off"), logoUrl, accent),

                _ => Result<bool>.Fail($"No delivery for {job.Payload.GetType().Name}.")
            };

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Couldn't deliver {Topic} notification to {UserId}: {Error}", job.Topic, job.UserId, result.Error);
                return;
            }

            _logger.LogInformation("Delivered {Topic} notification to {UserId}", job.Topic, job.UserId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error delivering {Topic} notification to {UserId}", job.Topic, job.UserId);
        }
    }

    // "today", "tomorrow" or "on Mon 5 Oct", in venue time.
    private string DayLabel(DateOnly date)
    {
        DateOnly today = RotaTime.Today(_timeProvider.GetUtcNow().UtcDateTime);

        return date == today ? "today"
            : date == today.AddDays(1) ? "tomorrow"
            : $"on {date.ToString("ddd d MMM", RotaFormat.Uk)}";
    }
}
