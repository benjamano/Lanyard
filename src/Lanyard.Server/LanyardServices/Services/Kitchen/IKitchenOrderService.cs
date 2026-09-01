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

    Task<Result<KitchenOrder>> MarkPaidAtTillAsync(int orderId);

    /// <summary>Counts for the glanceable dashboard widget: how many open, and how old the oldest is.</summary>
    Task<Result<KitchenOrderSummary>> GetOpenOrderSummaryAsync(int locationId);
}

/// <summary>Aggregate counts for the dashboard widget.</summary>
public record KitchenOrderSummary(int OpenOrderCount, int PreparingCount, int ReadyCount, DateTime? OldestOrderCreateDate);
