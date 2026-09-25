using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Notifications;

// Once a day, forgets devices nobody has opened Lanyard on for 90 days: phones that were replaced,
// browsers that were cleared. docs/DATA_RETENTION.md promises this.
public class PushDeviceCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PushDeviceCleanupHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan UnseenLimit = TimeSpan.FromDays(90);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<PushDeviceCleanupHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PushDeviceCleanupHostedService started");

        try
        {
            // Let startup settle before the first sweep.
            await Task.Delay(TimeSpan.FromMinutes(5), _timeProvider, stoppingToken);

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

        _logger.LogInformation("PushDeviceCleanupHostedService stopped");
    }

    private async Task SweepAsync()
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            DateTime cutoff = _timeProvider.GetUtcNow().UtcDateTime - UnseenLimit;

            Result<int> subscriptions = await scope.ServiceProvider.GetRequiredService<IPushSubscriptionService>().RemoveStaleAsync(cutoff);
            Result<int> installs = await scope.ServiceProvider.GetRequiredService<IAppInstallationService>().RemoveStaleAsync(cutoff);

            if (subscriptions.Data > 0 || installs.Data > 0)
            {
                _logger.LogInformation("Removed {Subscriptions} push devices and {Installs} app installs not seen for 90 days", subscriptions.Data, installs.Data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old push devices");
        }
    }
}
