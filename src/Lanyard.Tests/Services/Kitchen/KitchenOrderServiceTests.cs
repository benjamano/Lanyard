using Lanyard.Application.Services.Kitchen;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Kitchen;

[TestClass]
public class KitchenOrderServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    /// <summary>
    /// A payment service that behaves like a healthy Stripe by default, and can be told to fail.
    /// Nothing in these tests reaches the network.
    /// </summary>
    private static Mock<IOrderPaymentService> GetPaymentServiceMock(bool succeeds = true)
    {
        Mock<IOrderPaymentService> mock = new();

        mock.SetupGet(p => p.IsConfigured).Returns(true);

        mock.Setup(p => p.CreatePaymentIntentAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string account, int amount, Guid token, string label, CancellationToken _) => succeeds
                ? Result<OrderPaymentIntent>.Ok(new OrderPaymentIntent($"pi_{token:N}", $"pi_{token:N}_secret", "pk_test", account))
                : Result<OrderPaymentIntent>.Fail("Stripe said no."));

        mock.Setup(p => p.RefundAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        mock.Setup(p => p.IsPaymentSucceededAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        return mock;
    }

    private static KitchenOrderService GetService(
        DbContextOptions<ApplicationDbContext> options,
        Mock<IKitchenHubNotifier>? notifier = null,
        Mock<IOrderPaymentService>? payments = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new KitchenOrderService(
            factoryMock.Object,
            (notifier ?? new Mock<IKitchenHubNotifier>()).Object,
            (payments ?? GetPaymentServiceMock()).Object,
            new Mock<ILogger<KitchenOrderService>>().Object);
    }

    /// <summary>
    /// Places an order and takes the payment, leaving it as the kitchen would see it. Most tests
    /// care about an order that exists on the queue, not about the checkout mechanics.
    /// </summary>
    private static async Task<CreateOrderResultDto> PlaceAndPayAsync(
        KitchenOrderService service,
        Fixture fixture,
        params (int ItemId, int Quantity)[] lines)
    {
        Result<CreateOrderResultDto> placed = await service.CreateOrderAsync(OrderFor(fixture, lines), fixture.CompanyId);

        Assert.IsTrue(placed.Success, placed.Error);

        await service.ConfirmPaymentAsync($"pi_{placed.Data!.OrderToken:N}");

        return placed.Data;
    }

    private static MenuService GetMenuService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new MenuService(factoryMock.Object, new Mock<ILogger<MenuService>>().Object);
    }

    private sealed record Fixture(int CompanyId, int LocationId, string TableToken, int BurgerId, int ChipsId);

    private static async Task<Fixture> SeedVenueAsync(
        DbContextOptions<ApplicationDbContext> options,
        string companyName = "Play2Day",
        bool orderingEnabled = true,
        string? stripeAccountId = "acct_test")
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = companyName, IsActive = true, StripeAccountId = stripeAccountId };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        Location location = new()
        {
            CompanyId = company.Id,
            Name = "Ipswich",
            IsActive = true,
            OrderingEnabled = orderingEnabled
        };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        MenuCategory category = new() { LocationId = location.Id, Name = "Mains", IsActive = true };
        ctx.MenuCategories.Add(category);
        await ctx.SaveChangesAsync();

        MenuItem burger = new() { CategoryId = category.Id, Name = "Burger", PriceCents = 850, IsAvailable = true, IsActive = true, AllergensConfirmed = true, ContainsAllergens = Allergen.CerealsContainingGluten | Allergen.Milk };
        MenuItem chips = new() { CategoryId = category.Id, Name = "Chips", PriceCents = 300, IsAvailable = true, IsActive = true, AllergensConfirmed = true };
        ctx.MenuItems.AddRange(burger, chips);

        QrTableToken table = new()
        {
            LocationId = location.Id,
            Label = "Table 4",
            Token = "table-4-token",
            IsActive = true
        };
        ctx.QrTableTokens.Add(table);
        await ctx.SaveChangesAsync();

        return new Fixture(company.Id, location.Id, table.Token, burger.Id, chips.Id);
    }

    private static CreateOrderRequestDto OrderFor(Fixture fixture, params (int ItemId, int Quantity)[] lines) => new()
    {
        TableToken = fixture.TableToken,
        Lines = [.. lines.Select(l => new CreateOrderLineDto { MenuItemId = l.ItemId, Quantity = l.Quantity })]
    };

    [TestMethod]
    public async Task CreateOrderAsync_TotalsFromPricesAtOrderTime()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 2), (fixture.ChipsId, 1)), fixture.CompanyId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual((850 * 2) + 300, result.Data!.TotalCents);
        Assert.AreEqual("Table 4", result.Data.TableLabel);
    }

    /// <summary>
    /// The reason order lines snapshot their name and price rather than pointing at the live
    /// menu row: repricing at 7pm must not restate what a 6pm customer already agreed to pay.
    /// </summary>
    [TestMethod]
    public async Task CreateOrderAsync_LaterPriceChangeDoesNotRestateAnExistingOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        KitchenOrderService service = GetService(options);

        Result<CreateOrderResultDto> placed = await service
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        Assert.IsTrue(placed.Success, placed.Error);
        Assert.AreEqual(850, placed.Data!.TotalCents);

        await using (ApplicationDbContext ctx = new(options))
        {
            MenuItem burger = await ctx.MenuItems.FirstAsync(i => i.Id == fixture.BurgerId);
            burger.Name = "Double Burger";
            burger.PriceCents = 1200;
            await ctx.SaveChangesAsync();
        }

        Result<OrderStatusDto> status = await service.GetOrderStatusAsync(placed.Data.OrderToken, fixture.CompanyId);

        Assert.IsTrue(status.Success, status.Error);
        Assert.AreEqual(850, status.Data!.TotalCents);
        Assert.AreEqual("Burger", status.Data.Lines.Single().Name);
        Assert.AreEqual(850, status.Data.Lines.Single().UnitPriceCents);
    }

    /// <summary>
    /// The menu-version poll keeps a browsing phone up to date, but it is a courtesy. This is
    /// the check that actually stops the kitchen being handed something it ran out of.
    /// </summary>
    [TestMethod]
    public async Task CreateOrderAsync_RejectsItemMarkedUnavailableAfterTheMenuWasLoaded()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);

        await GetMenuService(options).SetItemAvailabilityAsync(fixture.BurgerId, false);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "Burger");
    }

    /// <summary>
    /// Tenant isolation is enforced in the service, not only at the edge, so a bug in Reach's
    /// hostname resolution fails closed instead of ordering against another company's venue.
    /// </summary>
    [TestMethod]
    public async Task CreateOrderAsync_RejectsTableTokenBelongingToAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture play2Day = await SeedVenueAsync(options);
        Fixture partyman = await SeedVenueAsync(options, companyName: "Partyman");

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(play2Day, (play2Day.BurgerId, 1)), partyman.CompanyId);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Table not found.", result.Error);
    }

    [TestMethod]
    public async Task GetOrderStatusAsync_DoesNotDiscloseAnotherCompanysOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture play2Day = await SeedVenueAsync(options);
        Fixture partyman = await SeedVenueAsync(options, companyName: "Partyman");
        KitchenOrderService service = GetService(options);

        Result<CreateOrderResultDto> placed = await service
            .CreateOrderAsync(OrderFor(play2Day, (play2Day.BurgerId, 1)), play2Day.CompanyId);

        Result<OrderStatusDto> status = await service.GetOrderStatusAsync(placed.Data!.OrderToken, partyman.CompanyId);

        Assert.IsFalse(status.Success);
        Assert.AreEqual("Order not found.", status.Error);
    }

    [TestMethod]
    public async Task CreateOrderAsync_RejectsOrderWhenVenueHasOrderingDisabled()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options, orderingEnabled: false);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task CreateOrderAsync_CollapsesRepeatedItemsIntoOneLine()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        KitchenOrderService service = GetService(options);

        Result<CreateOrderResultDto> placed = await service
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1), (fixture.BurgerId, 2)), fixture.CompanyId);

        Result<OrderStatusDto> status = await service.GetOrderStatusAsync(placed.Data!.OrderToken, fixture.CompanyId);

        OrderStatusLineDto line = status.Data!.Lines.Single();
        Assert.AreEqual(3, line.Quantity);
        Assert.AreEqual(850 * 3, status.Data.TotalCents);
    }

    [TestMethod]
    public async Task CreateOrderAsync_RejectsEmptyOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(fixture), fixture.CompanyId);

        Assert.IsFalse(result.Success);
    }

    /// <summary>
    /// The whole point of holding an order at AwaitingPayment: an unpaid order must not reach
    /// the kitchen, or a customer who abandons the checkout gets food cooked for them anyway.
    /// </summary>
    [TestMethod]
    public async Task CreateOrderAsync_DoesNotReachTheKitchenUntilPaid()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        Mock<IKitchenHubNotifier> notifier = new();
        KitchenOrderService service = GetService(options, notifier);

        Result<CreateOrderResultDto> placed = await service
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        Assert.IsTrue(placed.Success, placed.Error);
        Assert.IsFalse(string.IsNullOrEmpty(placed.Data!.ClientSecret), "The customer needs a client secret to pay.");

        notifier.Verify(n => n.NotifyOrderReceivedAsync(It.IsAny<int>(), It.IsAny<KitchenOrderTicketDto>()), Times.Never);

        Result<List<KitchenOrder>> queue = await service.GetOpenOrdersForLocationAsync(fixture.LocationId);
        Assert.AreEqual(0, queue.Data!.Count);
    }

    [TestMethod]
    public async Task ConfirmPaymentAsync_ReleasesTheOrderToTheKitchen()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        Mock<IKitchenHubNotifier> notifier = new();
        KitchenOrderService service = GetService(options, notifier);

        Result<CreateOrderResultDto> placed = await service
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        Result<KitchenOrder> confirmed = await service.ConfirmPaymentAsync($"pi_{placed.Data!.OrderToken:N}");

        Assert.IsTrue(confirmed.Success, confirmed.Error);
        Assert.AreEqual(KitchenOrderStatus.Received, confirmed.Data!.Status);
        Assert.AreEqual(KitchenOrderPaymentStatus.Paid, confirmed.Data.PaymentStatus);

        notifier.Verify(n => n.NotifyOrderReceivedAsync(
            fixture.LocationId,
            It.Is<KitchenOrderTicketDto>(t => t.TableLabel == "Table 4" && t.TotalCents == 850)),
            Times.Once);
    }

    /// <summary>
    /// Stripe retries webhooks and the customer's poll reconciles the same payment, so this
    /// runs more than once for one order. Twice must not mean two tickets.
    /// </summary>
    [TestMethod]
    public async Task ConfirmPaymentAsync_IsIdempotent()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        Mock<IKitchenHubNotifier> notifier = new();
        KitchenOrderService service = GetService(options, notifier);

        Result<CreateOrderResultDto> placed = await service
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        string paymentIntentId = $"pi_{placed.Data!.OrderToken:N}";

        await service.ConfirmPaymentAsync(paymentIntentId);
        await service.ConfirmPaymentAsync(paymentIntentId);
        await service.ConfirmPaymentAsync(paymentIntentId);

        notifier.Verify(n => n.NotifyOrderReceivedAsync(It.IsAny<int>(), It.IsAny<KitchenOrderTicketDto>()), Times.Once);

        Result<List<KitchenOrder>> queue = await service.GetOpenOrdersForLocationAsync(fixture.LocationId);
        Assert.AreEqual(1, queue.Data!.Count);
    }

    /// <summary>
    /// Webhooks can arrive late. One landing after staff cancelled must not put the ticket back.
    /// </summary>
    [TestMethod]
    public async Task ConfirmPaymentAsync_DoesNotResurrectACancelledOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        KitchenOrderService service = GetService(options);

        Result<CreateOrderResultDto> placed = await service
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        int orderId = await OrderIdForAsync(options, placed.Data!.OrderToken);
        await service.CancelOrderAsync(orderId, refund: false);

        await service.ConfirmPaymentAsync($"pi_{placed.Data.OrderToken:N}");

        Result<List<KitchenOrder>> queue = await service.GetOpenOrdersForLocationAsync(fixture.LocationId);
        Assert.AreEqual(0, queue.Data!.Count);
    }

    [TestMethod]
    public async Task CreateOrderAsync_RefusesWhenTheVenueHasNoPaymentAccount()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options, stripeAccountId: null);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        Assert.IsFalse(result.Success);
    }

    /// <summary>
    /// A Stripe failure must leave nothing behind - not an unpayable order the kitchen has to
    /// work out how to void.
    /// </summary>
    [TestMethod]
    public async Task CreateOrderAsync_WritesNoOrderWhenThePaymentCannotBeStarted()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);

        Result<CreateOrderResultDto> result = await GetService(options, payments: GetPaymentServiceMock(succeeds: false))
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        Assert.IsFalse(result.Success);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.KitchenOrders.CountAsync());
    }

    [TestMethod]
    public async Task CancelOrderAsync_RefundsWhenAsked()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        Mock<IOrderPaymentService> payments = GetPaymentServiceMock();
        KitchenOrderService service = GetService(options, payments: payments);

        CreateOrderResultDto placed = await PlaceAndPayAsync(service, fixture, (fixture.BurgerId, 1));
        int orderId = await OrderIdForAsync(options, placed.OrderToken);

        Result<KitchenOrder> cancelled = await service.CancelOrderAsync(orderId, refund: true);

        Assert.IsTrue(cancelled.Success, cancelled.Error);
        Assert.AreEqual(KitchenOrderPaymentStatus.Refunded, cancelled.Data!.PaymentStatus);
        payments.Verify(p => p.RefundAsync("acct_test", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CancelOrderAsync_DoesNotRefundWhenNotAsked()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        Mock<IOrderPaymentService> payments = GetPaymentServiceMock();
        KitchenOrderService service = GetService(options, payments: payments);

        CreateOrderResultDto placed = await PlaceAndPayAsync(service, fixture, (fixture.BurgerId, 1));
        int orderId = await OrderIdForAsync(options, placed.OrderToken);

        Result<KitchenOrder> cancelled = await service.CancelOrderAsync(orderId, refund: false);

        Assert.IsTrue(cancelled.Success, cancelled.Error);
        Assert.AreEqual(KitchenOrderPaymentStatus.Paid, cancelled.Data!.PaymentStatus);
        payments.Verify(p => p.RefundAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A refund that Stripe rejects must still let staff clear the ticket - otherwise the
    /// kitchen is stuck with an order it cannot remove - but must say the money needs handling.
    /// </summary>
    [TestMethod]
    public async Task CancelOrderAsync_StillCancelsWhenTheRefundFails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        Mock<IOrderPaymentService> payments = GetPaymentServiceMock();

        payments.Setup(p => p.RefundAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Fail("Charge already refunded."));

        KitchenOrderService service = GetService(options, payments: payments);

        CreateOrderResultDto placed = await PlaceAndPayAsync(service, fixture, (fixture.BurgerId, 1));
        int orderId = await OrderIdForAsync(options, placed.OrderToken);

        Result<KitchenOrder> cancelled = await service.CancelOrderAsync(orderId, refund: true);

        Assert.IsFalse(cancelled.Success);
        StringAssert.Contains(cancelled.Error!, "refund failed");

        Result<List<KitchenOrder>> queue = await service.GetOpenOrdersForLocationAsync(fixture.LocationId);
        Assert.AreEqual(0, queue.Data!.Count, "The ticket should have left the kitchen queue even though the refund failed.");
    }

    /// <summary>
    /// A per-line cap is trivially defeated by splitting one item across many lines, so the
    /// check has to happen after duplicates are collapsed.
    /// </summary>
    [TestMethod]
    public async Task CreateOrderAsync_RejectsAHugeQuantitySplitAcrossManyLines()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);

        (int, int)[] lines = [.. Enumerable.Repeat((fixture.BurgerId, 50), 40)];

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(fixture, lines), fixture.CompanyId);

        Assert.IsFalse(result.Success);
    }

    /// <summary>
    /// Removing a whole menu section leaves its items active, so without checking the category
    /// they stay orderable by anyone holding a stale menu.
    /// </summary>
    [TestMethod]
    public async Task CreateOrderAsync_RejectsItemsInARemovedMenuSection()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            MenuCategory category = await ctx.MenuCategories.FirstAsync();
            category.IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task GetStatsAsync_CountsServedAndTakings()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        KitchenOrderService service = GetService(options);

        CreateOrderResultDto first = await PlaceAndPayAsync(service, fixture, (fixture.BurgerId, 1));
        CreateOrderResultDto second = await PlaceAndPayAsync(service, fixture, (fixture.ChipsId, 2));

        int firstId = await OrderIdForAsync(options, first.OrderToken);
        await service.SetOrderStatusAsync(firstId, KitchenOrderStatus.Preparing);
        await service.SetOrderStatusAsync(firstId, KitchenOrderStatus.Ready);
        await service.SetOrderStatusAsync(firstId, KitchenOrderStatus.Completed);

        Result<KitchenStats> stats = await service.GetStatsAsync(fixture.LocationId, KitchenStatsPeriod.Today);

        Assert.IsTrue(stats.Success, stats.Error);
        Assert.AreEqual(1, stats.Data!.ServedCount);

        // Both orders are paid, so takings count both - served and paid are different questions.
        Assert.AreEqual(850 + (300 * 2), stats.Data.TakingsCents);
        Assert.AreEqual(0, stats.Data.CancelledCount);

        _ = second;
    }

    /// <summary>
    /// No average is not an average of zero. A widget showing "0 min" for a kitchen that has
    /// served nothing would read as impossibly fast rather than as no data.
    /// </summary>
    [TestMethod]
    public async Task GetStatsAsync_ReturnsNoAverageWhenNothingHasReachedReady()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        KitchenOrderService service = GetService(options);

        await PlaceAndPayAsync(service, fixture, (fixture.BurgerId, 1));

        Result<KitchenStats> stats = await service.GetStatsAsync(fixture.LocationId, KitchenStatsPeriod.Today);

        Assert.IsTrue(stats.Success, stats.Error);
        Assert.IsNull(stats.Data!.AverageSecondsToReady);
    }

    [TestMethod]
    public async Task GetStatsAsync_MeasuresTimeToReady()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        KitchenOrderService service = GetService(options);

        CreateOrderResultDto placed = await PlaceAndPayAsync(service, fixture, (fixture.BurgerId, 1));
        int orderId = await OrderIdForAsync(options, placed.OrderToken);

        // Backdate creation so the measured duration is not sub-second noise.
        await using (ApplicationDbContext ctx = new(options))
        {
            KitchenOrder order = await ctx.KitchenOrders.FirstAsync(o => o.Id == orderId);
            order.CreateDate = DateTime.UtcNow.AddMinutes(-5);
            await ctx.SaveChangesAsync();
        }

        await service.SetOrderStatusAsync(orderId, KitchenOrderStatus.Ready);

        Result<KitchenStats> stats = await service.GetStatsAsync(fixture.LocationId, KitchenStatsPeriod.Today);

        Assert.IsNotNull(stats.Data!.AverageSecondsToReady);
        Assert.IsTrue(stats.Data.AverageSecondsToReady > 250, $"Expected roughly five minutes, got {stats.Data.AverageSecondsToReady}s.");
    }

    /// <summary>
    /// Nudging a ticket between Preparing and Ready must not rewrite how long the food took.
    /// </summary>
    [TestMethod]
    public async Task SetOrderStatusAsync_StampsReadyDateOnlyOnce()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        KitchenOrderService service = GetService(options);

        CreateOrderResultDto placed = await PlaceAndPayAsync(service, fixture, (fixture.BurgerId, 1));
        int orderId = await OrderIdForAsync(options, placed.OrderToken);

        await service.SetOrderStatusAsync(orderId, KitchenOrderStatus.Ready);

        DateTime? firstReadyDate;
        await using (ApplicationDbContext ctx = new(options))
        {
            firstReadyDate = (await ctx.KitchenOrders.FirstAsync(o => o.Id == orderId)).ReadyDate;
        }

        Assert.IsNotNull(firstReadyDate);

        await service.SetOrderStatusAsync(orderId, KitchenOrderStatus.Preparing);
        await service.SetOrderStatusAsync(orderId, KitchenOrderStatus.Ready);

        await using (ApplicationDbContext ctx = new(options))
        {
            Assert.AreEqual(firstReadyDate, (await ctx.KitchenOrders.FirstAsync(o => o.Id == orderId)).ReadyDate);
        }
    }

    /// <summary>
    /// Refunded money went back, so a takings figure that still counted it would not reconcile
    /// with the venue's Stripe balance.
    /// </summary>
    [TestMethod]
    public async Task GetStatsAsync_ExcludesRefundedOrdersFromTakings()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        KitchenOrderService service = GetService(options);

        CreateOrderResultDto refunded = await PlaceAndPayAsync(service, fixture, (fixture.BurgerId, 1));
        await PlaceAndPayAsync(service, fixture, (fixture.ChipsId, 1));

        await service.CancelOrderAsync(await OrderIdForAsync(options, refunded.OrderToken), refund: true);

        Result<KitchenStats> stats = await service.GetStatsAsync(fixture.LocationId, KitchenStatsPeriod.Today);

        Assert.AreEqual(300, stats.Data!.TakingsCents);
        Assert.AreEqual(1, stats.Data.RefundedCount);
        Assert.AreEqual(1, stats.Data.CancelledCount);
    }

    [TestMethod]
    public async Task GetStatsAsync_IgnoresOtherVenues()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture play2Day = await SeedVenueAsync(options);
        Fixture partyman = await SeedVenueAsync(options, companyName: "Partyman");
        KitchenOrderService service = GetService(options);

        await PlaceAndPayAsync(service, play2Day, (play2Day.BurgerId, 1));

        Result<KitchenStats> stats = await service.GetStatsAsync(partyman.LocationId, KitchenStatsPeriod.Today);

        Assert.AreEqual(0, stats.Data!.TakingsCents);
        Assert.AreEqual(0, stats.Data.ServedCount);
    }

    /// <summary>
    /// The public menu hides undeclared items, but a phone holding an older menu must not be
    /// able to order one either - so the guard is repeated at order time rather than trusted.
    /// </summary>
    [TestMethod]
    public async Task CreateOrderAsync_RejectsItemsWhoseAllergensAreNotConfirmed()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            MenuItem burger = await ctx.MenuItems.FirstAsync(i => i.Id == fixture.BurgerId);
            burger.AllergensConfirmed = false;
            await ctx.SaveChangesAsync();
        }

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        Assert.IsFalse(result.Success);
    }

    /// <summary>
    /// Allergens are snapshotted with the line for the same reason as name and price: correcting
    /// the menu tomorrow must not rewrite what the customer was told, or what the ticket says to
    /// whoever hands the food over.
    /// </summary>
    [TestMethod]
    public async Task OrderLines_SnapshotAllergensAgainstLaterMenuEdits()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        KitchenOrderService service = GetService(options);

        CreateOrderResultDto placed = await PlaceAndPayAsync(service, fixture, (fixture.BurgerId, 1));

        await using (ApplicationDbContext ctx = new(options))
        {
            MenuItem burger = await ctx.MenuItems.FirstAsync(i => i.Id == fixture.BurgerId);
            burger.ContainsAllergens = Allergen.Peanuts;
            await ctx.SaveChangesAsync();
        }

        Result<OrderStatusDto> status = await service.GetOrderStatusAsync(placed.OrderToken, fixture.CompanyId);

        OrderStatusLineDto line = status.Data!.Lines.Single();
        Assert.IsTrue(line.ContainsAllergens.HasFlag(Allergen.Milk), "The order should keep the declaration the customer saw.");
        Assert.IsFalse(line.ContainsAllergens.HasFlag(Allergen.Peanuts), "A later menu edit must not rewrite a placed order.");
    }

    private static async Task<int> OrderIdForAsync(DbContextOptions<ApplicationDbContext> options, Guid orderToken)
    {
        await using ApplicationDbContext ctx = new(options);

        return (await ctx.KitchenOrders.FirstAsync(o => o.OrderToken == orderToken)).Id;
    }

    /// <summary>
    /// A kitchen display left open on a second screen must not be able to drag a finished
    /// ticket back into the queue.
    /// </summary>
    [TestMethod]
    public async Task SetOrderStatusAsync_RefusesToReopenACompletedOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        KitchenOrderService service = GetService(options);

        CreateOrderResultDto placed = await PlaceAndPayAsync(service, fixture, (fixture.BurgerId, 1));
        int orderId = await OrderIdForAsync(options, placed.OrderToken);

        Assert.IsTrue((await service.SetOrderStatusAsync(orderId, KitchenOrderStatus.Completed)).Success);

        Result<KitchenOrder> reopened = await service.SetOrderStatusAsync(orderId, KitchenOrderStatus.Preparing);

        Assert.IsFalse(reopened.Success);
        StringAssert.Contains(reopened.Error!, "completed");
    }

    [TestMethod]
    public async Task GetOpenOrderSummaryAsync_ExcludesCompletedAndCancelledOrders()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        KitchenOrderService service = GetService(options);

        CreateOrderResultDto first = await PlaceAndPayAsync(service, fixture, (fixture.BurgerId, 1));
        await PlaceAndPayAsync(service, fixture, (fixture.ChipsId, 1));

        int firstId = await OrderIdForAsync(options, first.OrderToken);

        await service.SetOrderStatusAsync(firstId, KitchenOrderStatus.Completed);

        Result<KitchenOrderSummary> summary = await service.GetOpenOrderSummaryAsync(fixture.LocationId);

        Assert.IsTrue(summary.Success, summary.Error);
        Assert.AreEqual(1, summary.Data!.OpenOrderCount);
    }

    [TestMethod]
    public async Task GetOpenOrderSummaryAsync_ReturnsZeroesForAQuietVenue()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);

        Result<KitchenOrderSummary> summary = await GetService(options).GetOpenOrderSummaryAsync(fixture.LocationId);

        Assert.IsTrue(summary.Success, summary.Error);
        Assert.AreEqual(0, summary.Data!.OpenOrderCount);
        Assert.IsNull(summary.Data.OldestOrderCreateDate);
    }
}
