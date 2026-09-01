using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Kitchen;

/// <summary>
/// Closes off orders that were created but never paid for.
///
/// Every customer who opens the checkout and then changes their mind, loses signal, or simply
/// puts their phone away leaves an order at AwaitingPayment. None of them reach the kitchen, so
/// nothing is broken - but nothing clears them either, and they accumulate for the life of the
/// deployment.
///
/// They are marked Cancelled/Failed rather than deleted. An abandoned order is still a record
/// that a customer tried to buy something, and a table with holes in its identity sequence is a
/// worse thing to debug than a few extra rows. Deleting payment-adjacent history is also the
/// kind of decision that is hard to reverse and easy to regret.
///
/// Same periodic-sweep shape as TrainingDueSoonHostedService.
/// </summary>
public class AbandonedOrderSweepHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<AbandonedOrderSweepHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// How long an unpaid order is left alone. Generously longer than any checkout takes, because
    /// the cost of sweeping too early - cancelling an order somebody is mid-way through paying
    /// for - is far higher than the cost of a row lingering an extra hour.
    /// </summary>
    private static readonly TimeSpan _abandonedAfter = TimeSpan.FromHours(2);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<AbandonedOrderSweepHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("AbandonedOrderSweepHostedService started");

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSweepAsync(stoppingToken);
                await Task.Delay(_sweepInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AbandonedOrderSweepHostedService stopped unexpectedly");
        }
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IDbContextFactory<ApplicationDbContext> factory =
                scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(stoppingToken);

            DateTime cutoff = DateTime.UtcNow - _abandonedAfter;

            // PaymentStatus is checked as well as Status: an order that was paid for but whose
            // webhook has not landed yet must never be swept, and Pending is the only state this
            // sweep has any business touching.
            List<KitchenOrder> abandoned = await ctx.KitchenOrders
                .Where(o => o.Status == KitchenOrderStatus.AwaitingPayment
                    && o.PaymentStatus == KitchenOrderPaymentStatus.Pending
                    && o.CreateDate < cutoff)
                .ToListAsync(stoppingToken);

            if (abandoned.Count == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;

            foreach (KitchenOrder order in abandoned)
            {
                order.Status = KitchenOrderStatus.Cancelled;
                order.PaymentStatus = KitchenOrderPaymentStatus.Failed;
                order.UpdateDate = now;
            }

            await ctx.SaveChangesAsync(stoppingToken);

            _logger.LogInformation("Closed {Count} order(s) abandoned before payment", abandoned.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Swallowed so one bad sweep does not stop the loop; the next one runs in an hour.
            _logger.LogError(ex, "Abandoned-order sweep failed");
        }
    }
}
