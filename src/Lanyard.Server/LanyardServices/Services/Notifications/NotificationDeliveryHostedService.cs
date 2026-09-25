using Lanyard.Infrastructure.DTO.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Notifications;

// Drains the notification queue one job at a time, each in its own scope so a scoped email
// client and DbContext are never shared across jobs. One failing job is logged by the deliverer
// and never stops the loop.
public class NotificationDeliveryHostedService(
    NotificationDispatcher dispatcher,
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationDeliveryHostedService> logger) : BackgroundService
{
    private readonly NotificationDispatcher _dispatcher = dispatcher;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<NotificationDeliveryHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NotificationDeliveryHostedService started");

        try
        {
            await foreach (NotificationJob job in _dispatcher.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    INotificationDeliverer deliverer = scope.ServiceProvider.GetRequiredService<INotificationDeliverer>();

                    await deliverer.DeliverAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deliver a {Topic} notification to {UserId}", job.Topic, job.UserId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("NotificationDeliveryHostedService stopped");
    }
}
