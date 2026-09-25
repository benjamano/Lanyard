using Lanyard.Infrastructure.DTO.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Notifications;

// Drains the notification queue, each job in its own scope so a scoped email client and DbContext
// are never shared across jobs. A few jobs run at once: a push service that is timing out can hold
// one job for close to a minute (retries and back-off), and it mustn't hold every email and shift
// reminder queued behind it. One failing job is logged by the deliverer and never stops the loop.
public class NotificationDeliveryHostedService(
    NotificationDispatcher dispatcher,
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationDeliveryHostedService> logger) : BackgroundService
{
    private readonly NotificationDispatcher _dispatcher = dispatcher;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<NotificationDeliveryHostedService> _logger = logger;

    public const int MaxConcurrentDeliveries = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NotificationDeliveryHostedService started");

        try
        {
            ParallelOptions options = new() { MaxDegreeOfParallelism = MaxConcurrentDeliveries, CancellationToken = stoppingToken };

            await Parallel.ForEachAsync(_dispatcher.Reader.ReadAllAsync(stoppingToken), options, async (job, token) =>
            {
                try
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    INotificationDeliverer deliverer = scope.ServiceProvider.GetRequiredService<INotificationDeliverer>();

                    await deliverer.DeliverAsync(job, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deliver a {Topic} notification to {UserId}", job.Topic, job.UserId);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("NotificationDeliveryHostedService stopped");
    }
}
