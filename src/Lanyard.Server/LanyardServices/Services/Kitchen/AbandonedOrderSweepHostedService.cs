using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
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

            // Pending is the only state this sweep has any business touching - but it is also
            // the state an order sits in when the customer *did* pay and the webhook never
            // arrived. Those two are indistinguishable from the database alone, which is why
            // each candidate is checked against Stripe below before anything is written off.
            var candidates = await ctx.KitchenOrders
                .Where(o => o.Status == KitchenOrderStatus.AwaitingPayment
                    && o.PaymentStatus == KitchenOrderPaymentStatus.Pending
                    && o.CreateDate < cutoff)
                .Select(o => new
                {
                    o.Id,
                    o.PaymentIntentId,
                    StripeAccountId = o.Location!.Company!.StripeAccountId
                })
                .ToListAsync(stoppingToken);

            if (candidates.Count == 0)
            {
                return;
            }

            IOrderPaymentService payments = scope.ServiceProvider.GetRequiredService<IOrderPaymentService>();
            IKitchenOrderService orders = scope.ServiceProvider.GetRequiredService<IKitchenOrderService>();

            List<int> toClose = [];
            int rescued = 0;

            foreach (var candidate in candidates)
            {
                // No PaymentIntent means checkout never reached Stripe, so there is nothing that
                // could have been charged and nothing to ask about.
                if (!string.IsNullOrWhiteSpace(candidate.PaymentIntentId)
                    && !string.IsNullOrWhiteSpace(candidate.StripeAccountId)
                    && payments.IsConfigured)
                {
                    Result<bool> succeeded = await payments.IsPaymentSucceededAsync(
                        candidate.StripeAccountId, candidate.PaymentIntentId, stoppingToken);

                    // Only a definite "no" is grounds for writing an order off. If Stripe cannot
                    // be reached, leave it for the next sweep: cancelling an order somebody has
                    // actually paid for takes their money and gives them nothing, and the row
                    // would then read "payment failed" to anyone investigating.
                    if (!succeeded.IsSuccess)
                    {
                        _logger.LogWarning(
                            "Left order {OrderId} alone: could not confirm with Stripe whether it was paid ({Error})",
                            candidate.Id, succeeded.Error);

                        continue;
                    }

                    if (succeeded.Data)
                    {
                        // Paid, and the webhook never landed. Put it through as a payment
                        // confirmation rather than cancelling it - the kitchen still owes this
                        // customer their food.
                        Result<KitchenOrder> confirmed = await orders.ConfirmPaymentAsync(candidate.PaymentIntentId);

                        if (confirmed.IsSuccess)
                        {
                            rescued++;

                            _logger.LogWarning(
                                "Order {OrderId} was paid but never confirmed by webhook; recovered by the sweep. "
                                + "Check the Stripe webhook endpoint is delivering.", candidate.Id);
                        }
                        else
                        {
                            _logger.LogError(
                                "Order {OrderId} is paid at Stripe but could not be confirmed: {Error}",
                                candidate.Id, confirmed.Error);
                        }

                        continue;
                    }
                }

                toClose.Add(candidate.Id);
            }

            if (toClose.Count > 0)
            {
                DateTime now = DateTime.UtcNow;

                List<KitchenOrder> abandoned = await ctx.KitchenOrders
                    .Where(o => toClose.Contains(o.Id))
                    .ToListAsync(stoppingToken);

                foreach (KitchenOrder order in abandoned)
                {
                    order.Status = KitchenOrderStatus.Cancelled;
                    order.PaymentStatus = KitchenOrderPaymentStatus.Failed;
                    order.UpdateDate = now;
                }

                await ctx.SaveChangesAsync(stoppingToken);
            }

            _logger.LogInformation(
                "Closed {Closed} order(s) abandoned before payment; recovered {Rescued} that were paid but unconfirmed",
                toClose.Count, rescued);
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
