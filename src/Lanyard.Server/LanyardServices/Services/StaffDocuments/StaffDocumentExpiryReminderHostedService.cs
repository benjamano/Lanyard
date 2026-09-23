using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.Branding;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services.StaffDocuments;

/// <summary>
/// Sweeps for active staff documents whose custom ReminderDate has arrived (and ExpiryDate hasn't
/// passed yet) and emails the affected staff member once, tracked via the document's own
/// ReminderSentDate flag. A re-upload (renewal) always creates a new StaffDocument row with
/// ReminderSentDate null, so it naturally becomes eligible again. Same periodic-sweep shape as
/// TrainingDueSoonHostedService/CourseRecurrenceHostedService.
/// </summary>
public class StaffDocumentExpiryReminderHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<StaffDocumentExpiryReminderHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromHours(12);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<StaffDocumentExpiryReminderHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("StaffDocumentExpiryReminderHostedService started");

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSweepAsync(stoppingToken);
                await Task.Delay(_sweepInterval, stoppingToken);
            }

            _logger.LogInformation("StaffDocumentExpiryReminderHostedService stopped");
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StaffDocumentExpiryReminderHostedService terminated with an unhandled exception");
        }
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IStaffDocumentService documentService = scope.ServiceProvider.GetRequiredService<IStaffDocumentService>();
        IEmailService emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        IDbContextFactory<ApplicationDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        EmailOptions emailOptions = scope.ServiceProvider.GetRequiredService<IOptions<EmailOptions>>().Value;
        ITrainingBrandingResolver brandingResolver = scope.ServiceProvider.GetRequiredService<ITrainingBrandingResolver>();

        Result<List<StaffDocument>> pendingResult = await documentService.GetDocumentsWithPendingRemindersAsync();

        if (!pendingResult.IsSuccess || pendingResult.Data is null)
        {
            _logger.LogWarning("Failed to load staff documents with pending reminders: {Error}", pendingResult.Error);
            return;
        }

        if (pendingResult.Data.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Processing {Count} staff document expiry reminders", pendingResult.Data.Count);

        await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(stoppingToken);

        foreach (StaffDocument document in pendingResult.Data)
        {
            try
            {
                UserProfile? user = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == document.UserId, stoppingToken);

                if (user is null)
                {
                    continue;
                }

                TrainingBranding branding = await brandingResolver.ResolveAsync(document.UserId, null, null);

                string accentColorHex = branding.AccentColorHex;

                // See MainLayout.ApplyBrandingAsync - the endpoint is cache-keyed by URL, so a
                // logo replacement needs a new URL to guarantee a fresh fetch.
                string? logoUrl = branding is { CompanyId: int companyId, LogoFileId: Guid logoFileId }
                    ? $"{emailOptions.PublicBaseUrl.TrimEnd('/')}/api/companies/{companyId}/logo?v={logoFileId:N}"
                    : null;

                // Marked sent before the email is sent, not after - ReminderSentDate is the
                // concurrency guard against two overlapping sweeps (or two app instances) both
                // picking up the same reminder. Marking first means a losing sweep is rejected
                // here and never sends a duplicate email; marking last would leave a window
                // where both sweeps could send before either recorded it.
                Result<bool> markResult = await documentService.MarkReminderSentAsync(document.Id);

                if (!markResult.IsSuccess)
                {
                    _logger.LogWarning("Skipping staff document expiry reminder for document {DocumentId} - already claimed or failed to record: {Error}",
                        document.Id, markResult.Error);
                    continue;
                }

                int daysUntilExpiry = Math.Max(0, (int)(document.ExpiryDate!.Value.Date - DateTime.UtcNow.Date).TotalDays);

                Result<bool> emailResult = await emailService.SendStaffDocumentExpiryReminderEmailAsync(
                    user,
                    document.StaffDocumentType?.Name ?? "your document",
                    document.ExpiryDate!.Value,
                    daysUntilExpiry,
                    logoUrl,
                    accentColorHex);

                if (!emailResult.IsSuccess)
                {
                    _logger.LogWarning("Failed to send staff document expiry reminder for document {DocumentId}: {Error}",
                        document.Id, emailResult.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing staff document expiry reminder for document {DocumentId}", document.Id);
            }
        }
    }
}
