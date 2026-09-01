using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;

namespace Lanyard.Application.Services.Kitchen;

/// <summary>
/// Places customer orders and drives them through the kitchen.
/// </summary>
public interface IKitchenOrderService
{
    /// <summary>
    /// Places an order against the table identified by the request's token.
    /// </summary>
    /// <param name="expectedCompanyId">
    /// The tenant whose site the order was placed from. The table token is checked against it,
    /// so a token belonging to one company can never be ordered against from another's domain
    /// even if a caller supplies it directly.
    /// </param>
    Task<Result<CreateOrderResultDto>> CreateOrderAsync(CreateOrderRequestDto request, int expectedCompanyId);

    /// <summary>Status for the customer holding this order's token.</summary>
    Task<Result<OrderStatusDto>> GetOrderStatusAsync(Guid orderToken, int expectedCompanyId);

    /// <summary>Kitchen display queue: everything not yet completed or cancelled, oldest first.</summary>
    Task<Result<List<KitchenOrder>>> GetOpenOrdersForLocationAsync(int locationId);

    Task<Result<KitchenOrder>> SetOrderStatusAsync(int orderId, KitchenOrderStatus status);

    /// <summary>
    /// Records that a payment succeeded and releases the order to the kitchen.
    ///
    /// Idempotent: Stripe retries webhooks, and the customer's own status poll can confirm the
    /// same payment concurrently, so this must not produce two tickets for one order.
    /// </summary>
    Task<Result<KitchenOrder>> ConfirmPaymentAsync(string paymentIntentId);

    /// <summary>Records that a payment failed, so the customer is told rather than left waiting.</summary>
    Task<Result<bool>> MarkPaymentFailedAsync(string paymentIntentId);

    /// <summary>
    /// Re-checks an order's payment directly with Stripe. Backstop for a webhook that is slow
    /// or lost, driven from the customer's status poll so their order is not stuck pending
    /// because of an infrastructure hiccup they cannot see.
    /// </summary>
    Task<Result<bool>> ReconcilePaymentAsync(Guid orderToken, int expectedCompanyId);

    /// <summary>Cancels an order and, when asked, refunds the customer in full.</summary>
    Task<Result<KitchenOrder>> CancelOrderAsync(int orderId, bool refund);

    /// <summary>Counts for the glanceable dashboard widget: how many open, and how old the oldest is.</summary>
    Task<Result<KitchenOrderSummary>> GetOpenOrderSummaryAsync(int locationId);

    /// <summary>
    /// How the kitchen has performed over a window - served, cancelled, how long food took, and
    /// what it took. Separate from the open-order summary because this reads completed history
    /// rather than the live queue, and the two are wanted on different screens.
    /// </summary>
    Task<Result<KitchenStats>> GetStatsAsync(int locationId, KitchenStatsPeriod period);
}

/// <summary>Aggregate counts for the dashboard widget.</summary>
public record KitchenOrderSummary(int OpenOrderCount, int PreparingCount, int ReadyCount, DateTime? OldestOrderCreateDate);

/// <summary>
/// Kitchen performance over a window.
/// </summary>
/// <param name="AverageSecondsToReady">
/// Received to ready, over orders that actually reached ready in the window. Null when none did -
/// which is different from zero, and a widget showing "0 min" for a kitchen that has served
/// nothing would be actively misleading.
/// </param>
public record KitchenStats(
    int ServedCount,
    int CancelledCount,
    int RefundedCount,
    int TakingsCents,
    double? AverageSecondsToReady);
