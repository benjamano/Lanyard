using Lanyard.Application.Services.Kitchen;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Kitchen;

/// <summary>
/// The sweep that closes off orders abandoned before payment.
///
/// The failure that matters here is not leaving a row behind - it is cancelling an order
/// somebody is halfway through paying for, so most of these assert what it must *not* touch.
/// </summary>
[TestClass]
public class AbandonedOrderSweepTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    /// <summary>
    /// Runs one sweep. The service is a BackgroundService with a one-hour loop, so it is driven
    /// through StartAsync/StopAsync rather than waiting on the timer.
    /// </summary>
    private static async Task SweepAsync(
        DbContextOptions<ApplicationDbContext> options,
        Mock<IOrderPaymentService>? payments = null,
        Mock<IKitchenOrderService>? orders = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        ServiceCollection services = new();
        services.AddSingleton(factoryMock.Object);
        services.AddSingleton((payments ?? UnpaidPaymentService()).Object);
        services.AddSingleton((orders ?? new Mock<IKitchenOrderService>()).Object);

        await using ServiceProvider provider = services.BuildServiceProvider();

        AbandonedOrderSweepHostedService sweep = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new Mock<ILogger<AbandonedOrderSweepHostedService>>().Object);

        await sweep.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await sweep.StopAsync(CancellationToken.None);
    }

    /// <summary>A Stripe that answers "no, that was never paid" - the ordinary abandoned case.</summary>
    private static Mock<IOrderPaymentService> UnpaidPaymentService()
    {
        Mock<IOrderPaymentService> mock = new();
        mock.SetupGet(p => p.IsConfigured).Returns(true);
        mock.Setup(p => p.IsPaymentSucceededAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Ok(false));

        return mock;
    }

    private static async Task<int> SeedOrderAsync(
        DbContextOptions<ApplicationDbContext> options,
        KitchenOrderStatus status,
        KitchenOrderPaymentStatus paymentStatus,
        TimeSpan age)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = "Play2Day", IsActive = true, StripeAccountId = "acct_test" };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        Location location = new() { CompanyId = company.Id, Name = $"Venue {Guid.NewGuid():N}", IsActive = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        KitchenOrder order = new()
        {
            LocationId = location.Id,
            OrderToken = Guid.NewGuid(),
            TableLabelSnapshot = "Table 1",
            Status = status,
            PaymentStatus = paymentStatus,
            TotalCents = 850,
            PaymentIntentId = $"pi_{Guid.NewGuid():N}",
            CreateDate = DateTime.UtcNow - age,
            UpdateDate = DateTime.UtcNow - age
        };

        ctx.KitchenOrders.Add(order);
        await ctx.SaveChangesAsync();

        return order.Id;
    }

    private static async Task<KitchenOrder> GetAsync(DbContextOptions<ApplicationDbContext> options, int id)
    {
        await using ApplicationDbContext ctx = new(options);

        return await ctx.KitchenOrders.FirstAsync(o => o.Id == id);
    }

    [TestMethod]
    public async Task Sweep_ClosesAnOrderAbandonedBeforePayment()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int id = await SeedOrderAsync(options, KitchenOrderStatus.AwaitingPayment,
            KitchenOrderPaymentStatus.Pending, TimeSpan.FromHours(5));

        await SweepAsync(options);

        KitchenOrder order = await GetAsync(options, id);
        Assert.AreEqual(KitchenOrderStatus.Cancelled, order.Status);
        Assert.AreEqual(KitchenOrderPaymentStatus.Failed, order.PaymentStatus);
    }

    /// <summary>
    /// The important one: a customer who is mid-checkout must not have their order cancelled
    /// underneath them.
    /// </summary>
    [TestMethod]
    public async Task Sweep_LeavesARecentUnpaidOrderAlone()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int id = await SeedOrderAsync(options, KitchenOrderStatus.AwaitingPayment,
            KitchenOrderPaymentStatus.Pending, TimeSpan.FromMinutes(5));

        await SweepAsync(options);

        Assert.AreEqual(KitchenOrderStatus.AwaitingPayment, (await GetAsync(options, id)).Status);
    }

    /// <summary>
    /// An old order that was actually paid for - a webhook that arrived late, say - is real work
    /// for the kitchen and must survive the sweep.
    /// </summary>
    [TestMethod]
    public async Task Sweep_LeavesAPaidOrderAlone()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int id = await SeedOrderAsync(options, KitchenOrderStatus.Received,
            KitchenOrderPaymentStatus.Paid, TimeSpan.FromHours(5));

        await SweepAsync(options);

        KitchenOrder order = await GetAsync(options, id);
        Assert.AreEqual(KitchenOrderStatus.Received, order.Status);
        Assert.AreEqual(KitchenOrderPaymentStatus.Paid, order.PaymentStatus);
    }

    /// <summary>
    /// Records are closed off, never removed - an abandoned order is still evidence a customer
    /// tried to buy something.
    /// </summary>
    /// <summary>
    /// The one that costs real money. Stripe took the payment, the webhook never arrived, and the
    /// customer closed the tab so no status poll ever reconciled it. Writing the order off here
    /// would charge somebody and give them nothing - and leave a row reading "payment failed" to
    /// whoever later investigated.
    /// </summary>
    [TestMethod]
    public async Task Sweep_RecoversAnOrderThatWasPaidButNeverConfirmed()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int id = await SeedOrderAsync(options, KitchenOrderStatus.AwaitingPayment,
            KitchenOrderPaymentStatus.Pending, TimeSpan.FromHours(5));

        Mock<IOrderPaymentService> paid = new();
        paid.SetupGet(p => p.IsConfigured).Returns(true);
        paid.Setup(p => p.IsPaymentSucceededAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        Mock<IKitchenOrderService> orders = new();
        orders.Setup(o => o.ConfirmPaymentAsync(It.IsAny<string>()))
            .ReturnsAsync(Result<KitchenOrder>.Ok(new KitchenOrder { LocationId = 1, TableLabelSnapshot = "Table 1" }));

        await SweepAsync(options, paid, orders);

        // Confirmed rather than cancelled, and the confirmation went through the normal path.
        orders.Verify(o => o.ConfirmPaymentAsync(It.IsAny<string>()), Times.Once);
        Assert.AreNotEqual(KitchenOrderStatus.Cancelled, (await GetAsync(options, id)).Status);
    }

    /// <summary>
    /// If Stripe cannot be reached, the order is left for the next sweep. An unreachable payment
    /// provider is not evidence that nobody paid.
    /// </summary>
    [TestMethod]
    public async Task Sweep_LeavesAnOrderAloneWhenStripeCannotBeReached()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int id = await SeedOrderAsync(options, KitchenOrderStatus.AwaitingPayment,
            KitchenOrderPaymentStatus.Pending, TimeSpan.FromHours(5));

        Mock<IOrderPaymentService> unreachable = new();
        unreachable.SetupGet(p => p.IsConfigured).Returns(true);
        unreachable.Setup(p => p.IsPaymentSucceededAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<bool>.Fail("Stripe unreachable"));

        await SweepAsync(options, unreachable);

        Assert.AreEqual(KitchenOrderStatus.AwaitingPayment, (await GetAsync(options, id)).Status);
    }

    [TestMethod]
    public async Task Sweep_DoesNotDeleteRows()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        await SeedOrderAsync(options, KitchenOrderStatus.AwaitingPayment,
            KitchenOrderPaymentStatus.Pending, TimeSpan.FromHours(9));

        await SweepAsync(options);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.KitchenOrders.CountAsync());
    }
}
