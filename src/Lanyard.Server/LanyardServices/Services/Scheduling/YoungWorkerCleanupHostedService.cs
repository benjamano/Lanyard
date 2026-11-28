using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

// Twice a day, deletes the date of birth of anyone who has turned 18 (see
// YoungWorkerService.RemoveExpiredAsync). Checking more than once a day means a restart never
// leaves a row for long; deleting the same expired rows twice is harmless.
public class YoungWorkerCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<YoungWorkerCleanupHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<YoungWorkerCleanupHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("YoungWorkerCleanupHostedService started");

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), _timeProvider, stoppingToken);

            using PeriodicTimer timer = new(Interval, _timeProvider);

            do
            {
                await RunAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("YoungWorkerCleanupHostedService stopped");
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            ISystemDbContextFactory factory = scope.ServiceProvider.GetRequiredService<ISystemDbContextFactory>();

            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(cancellationToken);

            int removed = await YoungWorkerService.RemoveExpiredAsync(ctx, RotaTime.Today(_timeProvider.GetUtcNow().UtcDateTime), cancellationToken);

            if (removed > 0)
            {
                _logger.LogInformation("Removed {Count} dates of birth of staff who have turned 18", removed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Young worker cleanup failed");
        }
    }
}
