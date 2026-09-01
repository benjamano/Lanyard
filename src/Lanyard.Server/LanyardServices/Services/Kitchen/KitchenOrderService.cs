using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Kitchen;

public class KitchenOrderService(
    IDbContextFactory<ApplicationDbContext> factory,
    IKitchenHubNotifier hubNotifier,
    ILogger<KitchenOrderService> logger) : IKitchenOrderService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IKitchenHubNotifier _hubNotifier = hubNotifier;
    private readonly ILogger<KitchenOrderService> _logger = logger;

    /// <summary>
    /// A single order cannot exceed this many of one item. Not a business rule so much as a
    /// guard against a fat finger or a scripted caller turning one tap into a 10,000-burger
    /// ticket that the kitchen then has to work out how to void.
    /// </summary>
    private const int MaxQuantityPerLine = 50;

    private const int MaxLinesPerOrder = 100;

    public async Task<Result<CreateOrderResultDto>> CreateOrderAsync(CreateOrderRequestDto request, int expectedCompanyId)
    {
        try
        {
            if (request.Lines.Count == 0)
            {
                return Result<CreateOrderResultDto>.Fail("Your order is empty.");
            }

            if (request.Lines.Count > MaxLinesPerOrder)
            {
                return Result<CreateOrderResultDto>.Fail("That order has too many separate items.");
            }

            if (request.Lines.Any(l => l.Quantity < 1 || l.Quantity > MaxQuantityPerLine))
            {
                return Result<CreateOrderResultDto>.Fail($"Quantities must be between 1 and {MaxQuantityPerLine}.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            var table = await ctx.QrTableTokens
                .AsNoTracking()
                .TagWithCallSite()
                .Where(t => t.Token == request.TableToken && t.IsActive)
                .Select(t => new
                {
                    t.Id,
                    t.Label,
                    t.LocationId,
                    LocationActive = t.Location!.IsActive,
                    t.Location.OrderingEnabled,
                    t.Location.CompanyId
                })
                .FirstOrDefaultAsync();

            if (table is null || !table.LocationActive)
            {
                return Result<CreateOrderResultDto>.Fail("Table not found.");
            }

            // Tenant isolation, enforced here and not only at the edge: the caller telling us
            // which tenant it is does not make it so, and this is the last point before a row
            // is written against a venue.
            if (table.CompanyId != expectedCompanyId)
            {
                _logger.LogWarning("Rejected order for table token belonging to company {TokenCompanyId} on company {RequestCompanyId}'s site",
                    table.CompanyId, expectedCompanyId);

                return Result<CreateOrderResultDto>.Fail("Table not found.");
            }

            if (!table.OrderingEnabled)
            {
                return Result<CreateOrderResultDto>.Fail("This venue is not taking orders at the moment.");
            }

            // Collapse duplicates so two taps of the same item become quantity 2 on one line
            // rather than two lines the kitchen has to mentally add together.
            Dictionary<int, int> requestedQuantities = request.Lines
                .GroupBy(l => l.MenuItemId)
                .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

            List<int> itemIds = [.. requestedQuantities.Keys];

            List<MenuItem> items = await ctx.MenuItems
                .AsNoTracking()
                .TagWithCallSite()
                .Where(i => itemIds.Contains(i.Id)
                    && i.IsActive
                    && i.Category != null
                    && i.Category.LocationId == table.LocationId)
                .ToListAsync();

            if (items.Count != itemIds.Count)
            {
                return Result<CreateOrderResultDto>.Fail("Some items are no longer on the menu. Please review your order.");
            }

            // Re-checked at order time rather than trusted from the menu the phone is holding.
            // The availability poll is a courtesy that keeps a browsing customer up to date; this
            // is the check that actually stops the kitchen being handed something it ran out of.
            List<MenuItem> unavailable = [.. items.Where(i => !i.IsAvailable)];

            if (unavailable.Count > 0)
            {
                string names = string.Join(", ", unavailable.Select(i => i.Name));

                return Result<CreateOrderResultDto>.Fail($"Sorry, we've just run out of: {names}. Please remove them and try again.");
            }

            DateTime now = DateTime.UtcNow;

            List<KitchenOrderItem> orderItems = [.. items.Select(i => new KitchenOrderItem
            {
                OrderId = 0,
                MenuItemId = i.Id,
                MenuItemNameSnapshot = i.Name,
                UnitPriceCentsSnapshot = i.PriceCents,
                Quantity = requestedQuantities[i.Id]
            })];

            KitchenOrder order = new()
            {
                LocationId = table.LocationId,
                OrderToken = Guid.NewGuid(),
                QrTableTokenId = table.Id,
                TableLabelSnapshot = table.Label,
                Status = KitchenOrderStatus.Received,
                PaymentStatus = KitchenOrderPaymentStatus.Unpaid,
                TotalCents = orderItems.Sum(i => i.UnitPriceCentsSnapshot * i.Quantity),
                CustomerNote = string.IsNullOrWhiteSpace(request.CustomerNote) ? null : request.CustomerNote.Trim(),
                CreateDate = now,
                UpdateDate = now,
                Items = orderItems
            };

            await ctx.KitchenOrders.AddAsync(order);
            await ctx.SaveChangesAsync();

            _logger.LogInformation("Order {OrderId} received for {TableLabel} at location {LocationId} ({LineCount} lines, {TotalCents}p)",
                order.Id, order.TableLabelSnapshot, order.LocationId, orderItems.Count, order.TotalCents);

            await _hubNotifier.NotifyOrderReceivedAsync(order.LocationId, ToTicket(order));

            return Result<CreateOrderResultDto>.Ok(new CreateOrderResultDto
            {
                OrderToken = order.OrderToken,
                TotalCents = order.TotalCents,
                TableLabel = order.TableLabelSnapshot
            });
        }
        catch (Exception ex)
        {
            return Result<CreateOrderResultDto>.Fail($"Failed to place order: {ex.Message}");
        }
    }

    public async Task<Result<OrderStatusDto>> GetOrderStatusAsync(Guid orderToken, int expectedCompanyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            KitchenOrder? order = await ctx.KitchenOrders
                .AsNoTracking()
                .TagWithCallSite()
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.OrderToken == orderToken);

            if (order is null)
            {
                return Result<OrderStatusDto>.Fail("Order not found.");
            }

            var location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(l => l.Id == order.LocationId)
                .Select(l => new { l.CompanyId, l.MenuVersion })
                .FirstOrDefaultAsync();

            // Same tenant check as order creation, for the same reason - and the same
            // indistinguishable "not found" so the poll cannot be used to test whether an order
            // token exists under some other tenant.
            if (location is null || location.CompanyId != expectedCompanyId)
            {
                return Result<OrderStatusDto>.Fail("Order not found.");
            }

            return Result<OrderStatusDto>.Ok(new OrderStatusDto
            {
                OrderToken = order.OrderToken,
                Status = order.Status,
                PaymentStatus = order.PaymentStatus,
                TableLabel = order.TableLabelSnapshot,
                TotalCents = order.TotalCents,
                CreateDate = order.CreateDate,
                MenuVersion = location.MenuVersion,
                Lines = [.. order.Items.Select(i => new OrderStatusLineDto
                {
                    Name = i.MenuItemNameSnapshot,
                    Quantity = i.Quantity,
                    UnitPriceCents = i.UnitPriceCentsSnapshot
                })]
            });
        }
        catch (Exception ex)
        {
            return Result<OrderStatusDto>.Fail($"Failed to retrieve order status: {ex.Message}");
        }
    }

    public async Task<Result<List<KitchenOrder>>> GetOpenOrdersForLocationAsync(int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<KitchenOrder> orders = await ctx.KitchenOrders
                .AsNoTracking()
                .TagWithCallSite()
                .Include(o => o.Items)
                .Where(o => o.LocationId == locationId
                    && o.Status != KitchenOrderStatus.Completed
                    && o.Status != KitchenOrderStatus.Cancelled)
                .OrderBy(o => o.CreateDate)
                .ToListAsync();

            return Result<List<KitchenOrder>>.Ok(orders);
        }
        catch (Exception ex)
        {
            return Result<List<KitchenOrder>>.Fail($"Failed to retrieve open orders: {ex.Message}");
        }
    }

    public async Task<Result<KitchenOrder>> SetOrderStatusAsync(int orderId, KitchenOrderStatus status)
    {
        try
        {
            if (status == KitchenOrderStatus.Unknown)
            {
                return Result<KitchenOrder>.Fail("Invalid order status.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            KitchenOrder? order = await ctx.KitchenOrders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order is null)
            {
                return Result<KitchenOrder>.Fail("Order not found.");
            }

            // Completed and Cancelled are terminal. Without this, a stale kitchen display - one
            // left open on another screen - could drag a finished ticket back into the queue.
            if (order.Status is KitchenOrderStatus.Completed or KitchenOrderStatus.Cancelled)
            {
                return Result<KitchenOrder>.Fail($"This order is already {order.Status.ToString().ToLowerInvariant()}.");
            }

            KitchenOrderStatus previous = order.Status;

            order.Status = status;
            order.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Order {OrderId} at location {LocationId} moved from {PreviousStatus} to {NewStatus}",
                order.Id, order.LocationId, previous, status);

            await _hubNotifier.NotifyOrderStatusChangedAsync(order.LocationId, ToTicket(order));

            return Result<KitchenOrder>.Ok(order);
        }
        catch (Exception ex)
        {
            return Result<KitchenOrder>.Fail($"Failed to update order status: {ex.Message}");
        }
    }

    public async Task<Result<KitchenOrder>> MarkPaidAtTillAsync(int orderId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            KitchenOrder? order = await ctx.KitchenOrders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order is null)
            {
                return Result<KitchenOrder>.Fail("Order not found.");
            }

            order.PaymentStatus = KitchenOrderPaymentStatus.PaidAtTill;
            order.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Order {OrderId} at location {LocationId} marked paid at till ({TotalCents}p)",
                order.Id, order.LocationId, order.TotalCents);

            await _hubNotifier.NotifyOrderStatusChangedAsync(order.LocationId, ToTicket(order));

            return Result<KitchenOrder>.Ok(order);
        }
        catch (Exception ex)
        {
            return Result<KitchenOrder>.Fail($"Failed to mark order paid: {ex.Message}");
        }
    }

    public async Task<Result<KitchenOrderSummary>> GetOpenOrderSummaryAsync(int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Aggregated in the database rather than by loading the tickets: the widget only
            // ever renders these four numbers, and it refreshes on every dashboard poll.
            var summary = await ctx.KitchenOrders
                .AsNoTracking()
                .TagWithCallSite()
                .Where(o => o.LocationId == locationId
                    && o.Status != KitchenOrderStatus.Completed
                    && o.Status != KitchenOrderStatus.Cancelled)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    OpenCount = g.Count(),
                    PreparingCount = g.Count(o => o.Status == KitchenOrderStatus.Preparing),
                    ReadyCount = g.Count(o => o.Status == KitchenOrderStatus.Ready),
                    Oldest = (DateTime?)g.Min(o => o.CreateDate)
                })
                .FirstOrDefaultAsync();

            return Result<KitchenOrderSummary>.Ok(summary is null
                ? new KitchenOrderSummary(0, 0, 0, null)
                : new KitchenOrderSummary(summary.OpenCount, summary.PreparingCount, summary.ReadyCount, summary.Oldest));
        }
        catch (Exception ex)
        {
            return Result<KitchenOrderSummary>.Fail($"Failed to retrieve order summary: {ex.Message}");
        }
    }

    public static KitchenOrderTicketDto ToTicket(KitchenOrder order) => new()
    {
        OrderId = order.Id,
        LocationId = order.LocationId,
        TableLabel = order.TableLabelSnapshot,
        Status = order.Status,
        PaymentStatus = order.PaymentStatus,
        TotalCents = order.TotalCents,
        CustomerNote = order.CustomerNote,
        CreateDate = order.CreateDate,
        Lines = [.. order.Items.Select(i => new OrderStatusLineDto
        {
            Name = i.MenuItemNameSnapshot,
            Quantity = i.Quantity,
            UnitPriceCents = i.UnitPriceCentsSnapshot
        })]
    };
}
