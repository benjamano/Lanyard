using Lanyard.Application.Services.Kitchen;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Kitchen;

/// <summary>
/// The 30-day purge of the free-text note customers type at checkout.
///
/// The field asks for "allergies, no onions", so it collects health information from members of
/// the public. What matters here is that the note goes and nothing else does: the order is still
/// a financial and food-safety record.
/// </summary>
[TestClass]
public class OrderNoteRetentionTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static async Task SweepAsync(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        ServiceCollection services = new();
        services.AddSingleton(factoryMock.Object);

        await using ServiceProvider provider = services.BuildServiceProvider();

        OrderNoteRetentionHostedService sweep = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new Mock<ILogger<OrderNoteRetentionHostedService>>().Object);

        await sweep.StartAsync(CancellationToken.None);
        await Task.Delay(400);
        await sweep.StopAsync(CancellationToken.None);
    }

    private static async Task<int> SeedOrderAsync(
        DbContextOptions<ApplicationDbContext> options, string? note, TimeSpan age)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = "Play2Day", IsActive = true };
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
            Status = KitchenOrderStatus.Completed,
            PaymentStatus = KitchenOrderPaymentStatus.Paid,
            TotalCents = 850,
            CustomerNote = note,
            CreateDate = DateTime.UtcNow - age,
            UpdateDate = DateTime.UtcNow - age,
            Items = [new KitchenOrderItem
            {
                OrderId = 0,
                MenuItemNameSnapshot = "Burger",
                UnitPriceCentsSnapshot = 850,
                ContainsAllergensSnapshot = Allergen.Milk,
                Quantity = 1
            }]
        };

        ctx.KitchenOrders.Add(order);
        await ctx.SaveChangesAsync();

        return order.Id;
    }

    [TestMethod]
    public async Task Retention_ClearsANoteOlderThanThirtyDays()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int id = await SeedOrderAsync(options, "Severe nut allergy, table 4", TimeSpan.FromDays(31));

        await SweepAsync(options);

        await using ApplicationDbContext ctx = new(options);
        Assert.IsNull((await ctx.KitchenOrders.FirstAsync(o => o.Id == id)).CustomerNote);
    }

    [TestMethod]
    public async Task Retention_LeavesARecentNoteAlone()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int id = await SeedOrderAsync(options, "No onions please", TimeSpan.FromDays(29));

        await SweepAsync(options);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual("No onions please", (await ctx.KitchenOrders.FirstAsync(o => o.Id == id)).CustomerNote);
    }

    /// <summary>
    /// The order is a financial and food-safety record. Only the free text goes - the lines,
    /// prices and allergen declarations have to survive a refund enquiry months later.
    /// </summary>
    [TestMethod]
    public async Task Retention_KeepsTheOrderItsLinesAndItsAllergenDeclaration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int id = await SeedOrderAsync(options, "Nut allergy", TimeSpan.FromDays(400));

        await SweepAsync(options);

        await using ApplicationDbContext ctx = new(options);
        KitchenOrder order = await ctx.KitchenOrders.Include(o => o.Items).FirstAsync(o => o.Id == id);

        Assert.AreEqual(1, await ctx.KitchenOrders.CountAsync());
        Assert.AreEqual(850, order.TotalCents);
        Assert.AreEqual(KitchenOrderPaymentStatus.Paid, order.PaymentStatus);
        Assert.AreEqual("Burger", order.Items.Single().MenuItemNameSnapshot);
        Assert.AreEqual(Allergen.Milk, order.Items.Single().ContainsAllergensSnapshot);
    }

    /// <summary>
    /// UpdateDate answers "when did this order last change". Housekeeping is not a change to the
    /// order, and moving it would make every old order look freshly touched the day this ran.
    /// </summary>
    [TestMethod]
    public async Task Retention_DoesNotTouchUpdateDate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int id = await SeedOrderAsync(options, "Nut allergy", TimeSpan.FromDays(60));

        DateTime before;
        await using (ApplicationDbContext ctx = new(options))
        {
            before = (await ctx.KitchenOrders.FirstAsync(o => o.Id == id)).UpdateDate;
        }

        await SweepAsync(options);

        await using ApplicationDbContext check = new(options);
        Assert.AreEqual(before, (await check.KitchenOrders.FirstAsync(o => o.Id == id)).UpdateDate);
    }

    [TestMethod]
    public async Task Retention_WorksThroughMoreThanOneBatch()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        // Comfortably past the 500-row batch size, to prove the loop keeps going.
        for (int i = 0; i < 620; i++)
        {
            await SeedOrderAsync(options, $"Note {i}", TimeSpan.FromDays(45));
        }

        await SweepAsync(options);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.KitchenOrders.CountAsync(o => o.CustomerNote != null));
    }
}
