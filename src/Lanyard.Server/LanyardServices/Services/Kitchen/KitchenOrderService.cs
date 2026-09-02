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
    IOrderPaymentService paymentService,
    ILogger<KitchenOrderService> logger) : IKitchenOrderService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IKitchenHubNotifier _hubNotifier = hubNotifier;
    private readonly IOrderPaymentService _paymentService = paymentService;
    private readonly ILogger<KitchenOrderService> _logger = logger;

    /// <summary>
    /// A single order cannot exceed this many of one item. Not a business rule so much as a
    /// guard against a fat finger or a scripted caller turning one tap into a 10,000-burger
    /// ticket that the kitchen then has to work out how to void.
    /// </summary>
    private const int MaxQuantityPerLine = 50;

    /// <summary>
    /// A validated choice together with the group it came from. Paired explicitly rather than
    /// read back through MenuItemOption.OptionGroup, which a no-tracking query is not obliged to
    /// populate - and the group's name goes on the kitchen ticket, so it cannot be null.
    /// </summary>
    private readonly record struct ChosenOption(MenuItemOptionGroup Group, MenuItemOption Option);

    /// <summary>
    /// One requested line after taps of the identical dish-and-choices have been merged. A class
    /// rather than a tuple because Quantity accumulates as duplicates are folded in.
    /// </summary>
    private sealed class RequestedLine(int menuItemId, List<int> optionIds, int quantity)
    {
        public int MenuItemId { get; } = menuItemId;

        /// <summary>Distinct and sorted, so the same choices in a different tap order are one line.</summary>
        public List<int> OptionIds { get; } = optionIds;

        public int Quantity { get; set; } = quantity;
    }

    /// <summary>
    /// Turns the option ids a phone sent into the actual menu rows, refusing anything that does
    /// not add up.
    ///
    /// Everything here is re-checked against the database rather than trusted from the request:
    /// the ids decide both what the customer is charged and what allergens the ticket declares,
    /// so a crafted request must not be able to attach a cheaper option to a dish, an option
    /// belonging to a different dish, or one whose allergens nobody has confirmed.
    /// </summary>
    private static Result<List<ChosenOption>> ResolveChosenOptions(MenuItem item, List<int> requestedOptionIds)
    {
        List<ChosenOption> chosen = [];

        // Only groups that still have at least one confirmed, active choice count as real. A
        // required group whose choices have all been withdrawn would otherwise be impossible to
        // satisfy and would block the dish with a confusing message.
        List<MenuItemOptionGroup> groups = [.. item.OptionGroups
            .Where(g => g.Options.Any(o => o.AllergensConfirmed))
            .OrderBy(g => g.SortOrder)];

        HashSet<int> knownOptionIds = [.. groups.SelectMany(g => g.Options).Select(o => o.Id)];

        // Caught before the per-group checks so an id belonging to another dish is reported as
        // exactly that, rather than as a confusing "you must choose a side".
        if (requestedOptionIds.Any(id => !knownOptionIds.Contains(id)))
        {
            return Result<List<ChosenOption>>.Fail(
                $"Some choices for {item.Name} are no longer available. Please review your order.");
        }

        foreach (MenuItemOptionGroup group in groups)
        {
            List<MenuItemOption> selected = [.. group.Options.Where(o => requestedOptionIds.Contains(o.Id))];

            if (selected.Count < group.MinSelections)
            {
                return Result<List<ChosenOption>>.Fail(
                    group.MinSelections == 1
                        ? $"\"{group.Name}\" is required for {item.Name}."
                        : $"Please choose at least {group.MinSelections} for \"{group.Name}\" on {item.Name}.");
            }

            if (selected.Count > group.MaxSelections)
            {
                return Result<List<ChosenOption>>.Fail(
                    $"You can choose at most {group.MaxSelections} for {group.Name} on {item.Name}.");
            }

            // Availability last, so "you picked too many" is not reported as "we ran out".
            List<MenuItemOption> soldOut = [.. selected.Where(o => !o.IsAvailable || !o.AllergensConfirmed)];

            if (soldOut.Count > 0)
            {
                return Result<List<ChosenOption>>.Fail(
                    $"Sorry, we've just run out of: {string.Join(", ", soldOut.Select(o => o.Name))}. Please choose something else.");
            }

            chosen.AddRange(selected.Select(o => new ChosenOption(group, o)));
        }

        return Result<List<ChosenOption>>.Ok(chosen);
    }


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

            if (request.Lines.Any(l => l.Quantity < 1))
            {
                return Result<CreateOrderResultDto>.Fail("Quantities must be at least 1.");
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
                    t.Location.CompanyId,
                    StripeAccountId = t.Location.Company!.StripeAccountId,
                    PublishedDocuments = t.Location.Company.LegalDocuments.Count(d => d.IsPublished)
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

            // Collapse duplicates so two taps of the same thing become quantity 2 on one line
            // rather than two lines the kitchen has to mentally add together. The identity of a
            // line is the dish *plus its choices*: chips-with-beans and chips-with-peas are two
            // different things to cook and must never be merged into one line of two.
            Dictionary<string, RequestedLine> requestedLines = [];

            foreach (CreateOrderLineDto line in request.Lines)
            {
                List<int> optionIds = [.. line.SelectedOptionIds.Distinct().OrderBy(id => id)];
                string key = $"{line.MenuItemId}:{string.Join(',', optionIds)}";

                if (requestedLines.TryGetValue(key, out RequestedLine? existing))
                {
                    existing.Quantity += line.Quantity;
                }
                else
                {
                    requestedLines[key] = new RequestedLine(line.MenuItemId, optionIds, line.Quantity);
                }
            }

            // Checked after collapsing, not before: per-line caps are trivially defeated by
            // splitting one item across many lines, which would turn a hundred legal-looking
            // lines into a single five-thousand-unit ticket. Summed across variants too, so
            // ordering the same dish forty times with a different side each time still counts.
            if (requestedLines.Values.GroupBy(l => l.MenuItemId).Any(g => g.Sum(l => l.Quantity) > MaxQuantityPerLine))
            {
                return Result<CreateOrderResultDto>.Fail($"You can order at most {MaxQuantityPerLine} of any one item.");
            }

            List<int> itemIds = [.. requestedLines.Values.Select(l => l.MenuItemId).Distinct()];

            List<MenuItem> items = await ctx.MenuItems
                .AsNoTracking()
                .TagWithCallSite()
                .Include(i => i.OptionGroups.Where(g => g.IsActive))
                    .ThenInclude(g => g.Options.Where(o => o.IsActive))
                .Where(i => itemIds.Contains(i.Id)
                    && i.IsActive
                    // Same guard as the public menu, repeated here rather than trusted from it:
                    // a phone holding a menu from before an item's allergens were withdrawn must
                    // not be able to order something with no declaration behind it.
                    && i.AllergensConfirmed
                    && i.Category != null
                    // The category's own IsActive matters too: removing a whole menu section
                    // leaves its items active, and without this they stay orderable by anyone
                    // holding a stale menu.
                    && i.Category.IsActive
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

            Dictionary<int, MenuItem> itemsById = items.ToDictionary(i => i.Id);
            List<KitchenOrderItem> orderItems = [];

            foreach (RequestedLine line in requestedLines.Values)
            {
                MenuItem item = itemsById[line.MenuItemId];

                Result<List<ChosenOption>> chosen = ResolveChosenOptions(item, line.OptionIds);

                if (!chosen.Success || chosen.Data is null)
                {
                    return Result<CreateOrderResultDto>.Fail(chosen.Error!);
                }

                // Price and allergens are both computed here from the menu rows, never taken from
                // anything the phone sent. The client is told what a choice costs so it can show
                // a total; it is not believed about it.
                int unitPrice = item.PriceCents + chosen.Data.Sum(c => c.Option.PriceDeltaCents);

                Allergen allergens = item.ContainsAllergens;

                foreach (ChosenOption choice in chosen.Data)
                {
                    allergens |= choice.Option.ContainsAllergens;
                }

                orderItems.Add(new KitchenOrderItem
                {
                    OrderId = 0,
                    MenuItemId = item.Id,
                    MenuItemNameSnapshot = item.Name,
                    UnitPriceCentsSnapshot = unitPrice,
                    ContainsAllergensSnapshot = allergens,
                    Quantity = line.Quantity,
                    Options = [.. chosen.Data.Select(c => new KitchenOrderItemOption
                    {
                        OrderItemId = 0,
                        MenuItemOptionId = c.Option.Id,
                        GroupNameSnapshot = c.Group.Name,
                        OptionNameSnapshot = c.Option.Name,
                        PriceDeltaCentsSnapshot = c.Option.PriceDeltaCents,
                        ContainsAllergensSnapshot = c.Option.ContainsAllergens
                    })]
                });
            }

            if (string.IsNullOrWhiteSpace(table.StripeAccountId))
            {
                return Result<CreateOrderResultDto>.Fail("This venue isn't set up to take payments yet. Please order at the till.");
            }

            // Selling to a consumer at a distance means telling them who they are buying from,
            // and on what terms, before they buy. Until every customer-facing document is
            // published those pages read "not published yet", so taking money anyway would charge
            // somebody against terms they were never shown.
            if (table.PublishedDocuments < System.Enum.GetValues<LegalDocumentType>().Length)
            {
                _logger.LogWarning(
                    "Refused an order for location {LocationId}: company {CompanyId} has not published all of its customer-facing documents",
                    table.LocationId, table.CompanyId);

                return Result<CreateOrderResultDto>.Fail("This venue isn't quite ready to take orders. Please order at the till.");
            }

            KitchenOrder order = new()
            {
                LocationId = table.LocationId,
                OrderToken = Guid.NewGuid(),
                QrTableTokenId = table.Id,
                TableLabelSnapshot = table.Label,
                // Not Received. The kitchen must not see this ticket until the money is
                // confirmed, or a customer who abandons the checkout page gets food cooked
                // for them anyway.
                Status = KitchenOrderStatus.AwaitingPayment,
                PaymentStatus = KitchenOrderPaymentStatus.Pending,
                TotalCents = orderItems.Sum(i => i.UnitPriceCentsSnapshot * i.Quantity),
                CustomerNote = string.IsNullOrWhiteSpace(request.CustomerNote) ? null : request.CustomerNote.Trim(),
                CreateDate = now,
                UpdateDate = now,
                Items = orderItems
            };

            // The payment is created before the row is written, so a Stripe failure leaves no
            // order behind at all - rather than an orphaned ticket the customer can never pay
            // for and the kitchen has to work out how to void.
            Result<OrderPaymentIntent> payment = await _paymentService.CreatePaymentIntentAsync(
                table.StripeAccountId, order.TotalCents, order.OrderToken, order.TableLabelSnapshot);

            if (!payment.IsSuccess || payment.Data is null)
            {
                return Result<CreateOrderResultDto>.Fail(payment.Error ?? "We couldn't start your payment. Please try again.");
            }

            order.PaymentIntentId = payment.Data.PaymentIntentId;

            await ctx.KitchenOrders.AddAsync(order);
            await ctx.SaveChangesAsync();

            _logger.LogInformation("Order {OrderId} created for {TableLabel} at location {LocationId} awaiting payment ({LineCount} lines, {TotalCents}p)",
                order.Id, order.TableLabelSnapshot, order.LocationId, orderItems.Count, order.TotalCents);

            // Deliberately no kitchen notification here - that happens on payment confirmation.
            return Result<CreateOrderResultDto>.Ok(new CreateOrderResultDto
            {
                OrderToken = order.OrderToken,
                TotalCents = order.TotalCents,
                TableLabel = order.TableLabelSnapshot,
                ClientSecret = payment.Data.ClientSecret,
                PublishableKey = payment.Data.PublishableKey,
                StripeAccountId = payment.Data.StripeAccountId
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
                    .ThenInclude(i => i.Options)
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
                    UnitPriceCents = i.UnitPriceCentsSnapshot,
                    ContainsAllergens = i.ContainsAllergensSnapshot,
                    Options = [.. i.Options.Select(o => new OrderLineOptionDto
                    {
                        GroupName = o.GroupNameSnapshot,
                        OptionName = o.OptionNameSnapshot,
                        PriceDeltaCents = o.PriceDeltaCentsSnapshot,
                        ContainsAllergens = o.ContainsAllergensSnapshot
                    })]
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
                    .ThenInclude(i => i.Options)
                // AwaitingPayment is excluded as well as the terminal states: an unpaid order is
                // not work the kitchen should be looking at, and may never become one.
                .Where(o => o.LocationId == locationId
                    && o.Status != KitchenOrderStatus.Completed
                    && o.Status != KitchenOrderStatus.Cancelled
                    && o.Status != KitchenOrderStatus.AwaitingPayment)
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
                    .ThenInclude(i => i.Options)
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

            // Cancelling goes through CancelOrderAsync, which also decides about the refund.
            if (status == KitchenOrderStatus.Cancelled)
            {
                return Result<KitchenOrder>.Fail("Use CancelOrderAsync to cancel an order.");
            }

            if (order.Status == KitchenOrderStatus.AwaitingPayment)
            {
                return Result<KitchenOrder>.Fail("This order hasn't been paid for yet.");
            }

            KitchenOrderStatus previous = order.Status;

            order.Status = status;
            order.UpdateDate = DateTime.UtcNow;

            // Stamped once. Nudging a ticket back and forth between Preparing and Ready must not
            // rewrite how long the food actually took.
            if (status == KitchenOrderStatus.Ready && order.ReadyDate is null)
            {
                order.ReadyDate = order.UpdateDate;
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Order {OrderId} at location {LocationId} moved from {PreviousStatus} to {NewStatus}",
                order.Id, order.LocationId, previous, status);

            // Notified outside the try that wraps the commit: the status change is already
            // persisted, so a SignalR failure must not be reported as the update failing and
            // invite staff to tap the button again.
            KitchenOrder saved = order;

            await NotifySafelyAsync(
                () => _hubNotifier.NotifyOrderStatusChangedAsync(saved.LocationId, ToTicket(saved)),
                saved.Id);

            return Result<KitchenOrder>.Ok(order);
        }
        catch (Exception ex)
        {
            return Result<KitchenOrder>.Fail($"Failed to update order status: {ex.Message}");
        }
    }

    public async Task<Result<KitchenOrder>> ConfirmPaymentAsync(string paymentIntentId)
    {
        KitchenOrder order;
        bool alreadyConfirmed;

        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            KitchenOrder? found = await ctx.KitchenOrders
                .Include(o => o.Items)
                    .ThenInclude(i => i.Options)
                .FirstOrDefaultAsync(o => o.PaymentIntentId == paymentIntentId);

            if (found is null)
            {
                return Result<KitchenOrder>.Fail("No order matches that payment.");
            }

            // Stripe retries webhooks, and the customer's poll can reconcile the same payment at
            // the same moment. Confirming twice would announce one order to the kitchen twice.
            //
            // This read-then-write leaves a narrow window in which both callers see "not yet
            // paid" and both announce. A conditional UPDATE would close it, but ExecuteUpdate is
            // unsupported by the EF InMemory provider the whole test suite runs on, so it would
            // trade a rare duplicate notification for untestable payment code. The duplicate is
            // instead absorbed where it would do harm: KitchenDisplay keys arriving tickets on
            // OrderId, so a second announcement of the same order changes nothing on the rail.
            //
            // Setting Paid twice is harmless in itself - the money and the row end up identical.
            alreadyConfirmed = found.PaymentStatus == KitchenOrderPaymentStatus.Paid;

            if (!alreadyConfirmed)
            {
                DateTime now = DateTime.UtcNow;

                found.PaymentStatus = KitchenOrderPaymentStatus.Paid;
                found.PaidDate = now;
                found.UpdateDate = now;

                // Only a still-awaiting order advances. A late webhook must not resurrect an
                // order staff have already cancelled.
                if (found.Status == KitchenOrderStatus.AwaitingPayment)
                {
                    found.Status = KitchenOrderStatus.Received;
                }

                await ctx.SaveChangesAsync();
            }

            order = found;
        }
        catch (Exception ex)
        {
            return Result<KitchenOrder>.Fail($"Failed to confirm payment: {ex.Message}");
        }

        if (!alreadyConfirmed && order.Status == KitchenOrderStatus.Received)
        {
            _logger.LogInformation("Order {OrderId} paid ({TotalCents}p) and released to the kitchen at location {LocationId}",
                order.Id, order.TotalCents, order.LocationId);

            // Outside the transaction on purpose: the money has been taken and the order is
            // committed, so a SignalR failure must not turn a successful payment into a
            // reported failure that the customer retries into a second charge.
            await NotifySafelyAsync(
                () => _hubNotifier.NotifyOrderReceivedAsync(order.LocationId, ToTicket(order)),
                order.Id);
        }

        return Result<KitchenOrder>.Ok(order);
    }

    public async Task<Result<bool>> MarkPaymentFailedAsync(string paymentIntentId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            KitchenOrder? order = await ctx.KitchenOrders
                .FirstOrDefaultAsync(o => o.PaymentIntentId == paymentIntentId);

            if (order is null)
            {
                return Result<bool>.Fail("No order matches that payment.");
            }

            // A failure arriving after a success is ignored rather than applied - Stripe can
            // deliver events out of order, and money already taken outranks a stale failure.
            if (order.PaymentStatus == KitchenOrderPaymentStatus.Paid)
            {
                return Result<bool>.Ok(false);
            }

            order.PaymentStatus = KitchenOrderPaymentStatus.Failed;
            order.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Payment failed for order {OrderId} at location {LocationId}", order.Id, order.LocationId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to record payment failure: {ex.Message}");
        }
    }

    public async Task<Result<bool>> ReconcilePaymentAsync(Guid orderToken, int expectedCompanyId)
    {
        try
        {
            string paymentIntentId;
            string stripeAccountId;

            await using (ApplicationDbContext ctx = await _factory.CreateDbContextAsync())
            {
                var pending = await ctx.KitchenOrders
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(o => o.OrderToken == orderToken
                        && o.PaymentStatus == KitchenOrderPaymentStatus.Pending
                        && o.PaymentIntentId != null)
                    .Select(o => new
                    {
                        o.PaymentIntentId,
                        CompanyId = o.Location!.CompanyId,
                        StripeAccountId = o.Location.Company!.StripeAccountId
                    })
                    .FirstOrDefaultAsync();

                // Nothing to reconcile is the normal case, not an error - most polls arrive
                // for an order that is already paid.
                if (pending is null || pending.CompanyId != expectedCompanyId || pending.StripeAccountId is null)
                {
                    return Result<bool>.Ok(false);
                }

                paymentIntentId = pending.PaymentIntentId!;
                stripeAccountId = pending.StripeAccountId;
            }

            Result<bool> succeeded = await _paymentService.IsPaymentSucceededAsync(stripeAccountId, paymentIntentId);

            if (!succeeded.IsSuccess || !succeeded.Data)
            {
                return Result<bool>.Ok(false);
            }

            Result<KitchenOrder> confirmed = await ConfirmPaymentAsync(paymentIntentId);

            return confirmed.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(confirmed.Error!);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to reconcile payment: {ex.Message}");
        }
    }

    public async Task<Result<KitchenOrder>> CancelOrderAsync(int orderId, bool refund)
    {
        KitchenOrder order;
        string? refundFailure = null;

        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            KitchenOrder? found = await ctx.KitchenOrders
                .Include(o => o.Items)
                    .ThenInclude(i => i.Options)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (found is null)
            {
                return Result<KitchenOrder>.Fail("Order not found.");
            }

            if (found.Status is KitchenOrderStatus.Completed or KitchenOrderStatus.Cancelled)
            {
                return Result<KitchenOrder>.Fail($"This order is already {found.Status.ToString().ToLowerInvariant()}.");
            }

            if (refund && found.PaymentStatus == KitchenOrderPaymentStatus.Paid && found.PaymentIntentId is not null)
            {
                string? stripeAccountId = await ctx.Locations
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(l => l.Id == found.LocationId)
                    .Select(l => l.Company!.StripeAccountId)
                    .FirstOrDefaultAsync();

                Result<bool> refunded = stripeAccountId is null
                    ? Result<bool>.Fail("This venue has no payment account configured.")
                    : await _paymentService.RefundAsync(stripeAccountId, found.PaymentIntentId);

                if (refunded.IsSuccess)
                {
                    found.PaymentStatus = KitchenOrderPaymentStatus.Refunded;
                    found.RefundedDate = DateTime.UtcNow;
                }
                else
                {
                    // The cancellation still goes through. Refusing to cancel because the
                    // refund failed would leave a ticket the kitchen cannot clear; instead the
                    // order is cancelled and staff are told the money still needs handling.
                    refundFailure = refunded.Error;
                }
            }

            found.Status = KitchenOrderStatus.Cancelled;
            found.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            order = found;
        }
        catch (Exception ex)
        {
            return Result<KitchenOrder>.Fail($"Failed to cancel order: {ex.Message}");
        }

        _logger.LogInformation("Order {OrderId} at location {LocationId} cancelled (payment now {PaymentStatus})",
            order.Id, order.LocationId, order.PaymentStatus);

        await NotifySafelyAsync(
            () => _hubNotifier.NotifyOrderStatusChangedAsync(order.LocationId, ToTicket(order)),
            order.Id);

        return refundFailure is null
            ? Result<KitchenOrder>.Ok(order)
            : Result<KitchenOrder>.Fail($"Order cancelled, but the refund failed: {refundFailure}");
    }

    /// <summary>
    /// Runs a hub notification without letting its failure surface as the caller's failure.
    ///
    /// Every call site here has already committed - money taken, status changed - so reporting
    /// a SignalR problem as the operation failing would invite exactly the wrong retry.
    /// </summary>
    private async Task NotifySafelyAsync(Func<Task> notify, int orderId)
    {
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order {OrderId} was saved but the kitchen display could not be notified", orderId);
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
                    && o.Status != KitchenOrderStatus.Cancelled
                    && o.Status != KitchenOrderStatus.AwaitingPayment)
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

    public async Task<Result<KitchenStats>> GetStatsAsync(int locationId, KitchenStatsPeriod period)
    {
        try
        {
            DateTime since = PeriodStart(period);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            var stats = await ctx.KitchenOrders
                .AsNoTracking()
                .TagWithCallSite()
                .Where(o => o.LocationId == locationId && o.CreateDate >= since)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    ServedCount = g.Count(o => o.Status == KitchenOrderStatus.Completed),
                    CancelledCount = g.Count(o => o.Status == KitchenOrderStatus.Cancelled),
                    RefundedCount = g.Count(o => o.PaymentStatus == KitchenOrderPaymentStatus.Refunded),

                    // Refunded orders are excluded: that money went back, and a takings figure
                    // that still counted it would not reconcile with the venue's Stripe balance.
                    TakingsCents = g
                        .Where(o => o.PaymentStatus == KitchenOrderPaymentStatus.Paid)
                        .Sum(o => (int?)o.TotalCents) ?? 0
                })
                .FirstOrDefaultAsync();

            // Averaged in memory rather than in SQL. Date arithmetic is the least portable thing
            // EF translates - the SQL Server helpers do not exist for Npgsql, and the InMemory
            // provider the tests use translates differently again. This pulls two timestamps per
            // completed order for one venue over one window, which is small enough not to care.
            // Projected into an anonymous type, not a ValueTuple: Npgsql cannot read a tuple out
            // of a row and throws at runtime, while the InMemory provider the tests use accepts
            // it happily - so this only fails against a real database.
            var readyTimes = await ctx.KitchenOrders
                .AsNoTracking()
                .TagWithCallSite()
                .Where(o => o.LocationId == locationId && o.CreateDate >= since && o.ReadyDate != null)
                .Select(o => new { o.CreateDate, ReadyDate = o.ReadyDate!.Value })
                .ToListAsync();

            double? averageSecondsToReady = readyTimes.Count == 0
                ? null
                : readyTimes.Average(t => (t.ReadyDate - t.CreateDate).TotalSeconds);

            return Result<KitchenStats>.Ok(stats is null
                ? new KitchenStats(0, 0, 0, 0, averageSecondsToReady)
                : new KitchenStats(
                    stats.ServedCount,
                    stats.CancelledCount,
                    stats.RefundedCount,
                    stats.TakingsCents,
                    averageSecondsToReady));
        }
        catch (Exception ex)
        {
            return Result<KitchenStats>.Fail($"Failed to retrieve kitchen stats: {ex.Message}");
        }
    }

    /// <summary>
    /// Start of the window, in UTC.
    /// </summary>
    /// <remarks>
    /// "Today" means the server's local day, not the UTC day - a service running past midnight
    /// UTC in British Summer Time would otherwise reset the kitchen's figures mid-evening. This
    /// assumes the server shares the venue's timezone, which holds for a single-region
    /// deployment and would need a per-venue timezone if that ever stops being true.
    /// </remarks>
    private static DateTime PeriodStart(KitchenStatsPeriod period) => period switch
    {
        KitchenStatsPeriod.LastHour => DateTime.UtcNow.AddHours(-1),
        KitchenStatsPeriod.ThisWeek => DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek).ToUniversalTime(),
        _ => DateTime.Today.ToUniversalTime()
    };

    public async Task<Result<int>> GetLocationIdForOrderAsync(int orderId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            int? locationId = await ctx.KitchenOrders
                .AsNoTracking()
                .TagWithCallSite()
                .Where(o => o.Id == orderId)
                .Select(o => (int?)o.LocationId)
                .FirstOrDefaultAsync();

            return locationId is null
                ? Result<int>.Fail("Order not found.")
                : Result<int>.Ok(locationId.Value);
        }
        catch (Exception ex)
        {
            return Result<int>.Fail($"Failed to resolve the order's venue: {ex.Message}");
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
            UnitPriceCents = i.UnitPriceCentsSnapshot,
            ContainsAllergens = i.ContainsAllergensSnapshot,
            Options = [.. i.Options.Select(o => new OrderLineOptionDto
            {
                GroupName = o.GroupNameSnapshot,
                OptionName = o.OptionNameSnapshot,
                PriceDeltaCents = o.PriceDeltaCentsSnapshot,
                ContainsAllergens = o.ContainsAllergensSnapshot
            })]
        })]
    };
}
