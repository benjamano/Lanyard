using Lanyard.Infrastructure.DataAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Chat;

// Once a day, deletes chat that has passed the two-year retention period
// (ChatRetention.PurgeExpiredAsync; docs/DATA_RETENTION.md promises this).
public class ChatRetentionHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ChatRetentionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ChatRetentionHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ChatRetentionHostedService started");

        try
        {
            // Let startup settle before the first sweep.
            await Task.Delay(TimeSpan.FromMinutes(10), _timeProvider, stoppingToken);

            using PeriodicTimer timer = new(Interval, _timeProvider);

            do
            {
                await SweepAsync();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("ChatRetentionHostedService stopped");
    }

    private async Task SweepAsync()
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IDbContextFactory<ApplicationDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            ChatRetention.PurgeResult result = await ChatRetention.PurgeExpiredAsync(ctx, _timeProvider.GetUtcNow().UtcDateTime);

            if (result.Total > 0)
            {
                _logger.LogInformation(
                    "Chat retention: deleted {Messages} messages, emptied {Emptied} still under report, removed {Reports} reports, {Suspensions} suspensions and {Conversations} empty conversations older than two years",
                    result.MessagesDeleted, result.MessagesEmptied, result.Reports, result.Suspensions, result.Conversations);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting chat past its retention period");
        }
    }
}
