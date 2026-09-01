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

    private static KitchenOrderService GetService(
        DbContextOptions<ApplicationDbContext> options,
        Mock<IKitchenHubNotifier>? notifier = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new KitchenOrderService(
            factoryMock.Object,
            (notifier ?? new Mock<IKitchenHubNotifier>()).Object,
            new Mock<ILogger<KitchenOrderService>>().Object);
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
        bool orderingEnabled = true)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = companyName, IsActive = true };
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

        MenuItem burger = new() { CategoryId = category.Id, Name = "Burger", PriceCents = 850, IsAvailable = true, IsActive = true };
        MenuItem chips = new() { CategoryId = category.Id, Name = "Chips", PriceCents = 300, IsAvailable = true, IsActive = true };
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

    [TestMethod]
    public async Task CreateOrderAsync_PushesTheTicketToTheKitchenDisplay()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        Mock<IKitchenHubNotifier> notifier = new();

        await GetService(options, notifier)
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        notifier.Verify(n => n.NotifyOrderReceivedAsync(
            fixture.LocationId,
            It.Is<KitchenOrderTicketDto>(t => t.TableLabel == "Table 4" && t.TotalCents == 850)),
            Times.Once);
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

        Result<CreateOrderResultDto> placed = await service
            .CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);

        int orderId;
        await using (ApplicationDbContext ctx = new(options))
        {
            orderId = (await ctx.KitchenOrders.FirstAsync(o => o.OrderToken == placed.Data!.OrderToken)).Id;
        }

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

        Result<CreateOrderResultDto> first = await service.CreateOrderAsync(OrderFor(fixture, (fixture.BurgerId, 1)), fixture.CompanyId);
        await service.CreateOrderAsync(OrderFor(fixture, (fixture.ChipsId, 1)), fixture.CompanyId);

        int firstId;
        await using (ApplicationDbContext ctx = new(options))
        {
            firstId = (await ctx.KitchenOrders.FirstAsync(o => o.OrderToken == first.Data!.OrderToken)).Id;
        }

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
