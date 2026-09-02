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

/// <summary>
/// Ordering a dish that has choices on it: "chips, nuggets and beans" versus "... and peas".
///
/// The theme running through these is that the choices decide both the price and the allergen
/// declaration, so none of it may be taken from what the phone sent. Every test here either
/// checks the server recomputes something, or checks it refuses a request that does not add up.
/// </summary>
[TestClass]
public class MenuItemOptionOrderingTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static Mock<IOrderPaymentService> GetPaymentServiceMock()
    {
        Mock<IOrderPaymentService> mock = new();

        mock.SetupGet(p => p.IsConfigured).Returns(true);

        mock.Setup(p => p.CreatePaymentIntentAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string account, int amount, Guid token, string label, CancellationToken _) =>
                Result<OrderPaymentIntent>.Ok(new OrderPaymentIntent($"pi_{token:N}", $"pi_{token:N}_secret", "pk_test", account)));

        return mock;
    }

    private static KitchenOrderService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new KitchenOrderService(
            factoryMock.Object,
            new Mock<IKitchenHubNotifier>().Object,
            GetPaymentServiceMock().Object,
            new Mock<ILogger<KitchenOrderService>>().Object);
    }

    private sealed record Fixture(
        int CompanyId,
        int LocationId,
        string TableToken,
        int MealId,
        int SideGroupId,
        int BeansId,
        int PeasId,
        int CheeseId);

    /// <summary>
    /// A kids' meal with a required "choose your side" group (beans or peas) and an optional
    /// paid extra. Beans carry an allergen the meal itself does not, which is what makes the
    /// combined declaration worth asserting.
    /// </summary>
    private static async Task<Fixture> SeedMealAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new()
        {
            Name = "Play2Day",
            IsActive = true,
            StripeAccountId = "acct_test",
            // Required before a venue may take orders at all - see
            // KitchenOrderServiceTests.CreateOrderAsync_RefusesWhenTheCompanyHasNotPublishedItsLegalDetails.
            LegalName = "Play2Day Leisure Ltd",
            RegisteredAddress = "1 Cardinal Park, Ipswich, IP1 1AA",
            ContactEmail = "hello@example.test"
        };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        Location location = new() { CompanyId = company.Id, Name = "Ipswich", IsActive = true, OrderingEnabled = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        MenuCategory category = new() { LocationId = location.Id, Name = "Kids", IsActive = true };
        ctx.MenuCategories.Add(category);
        await ctx.SaveChangesAsync();

        MenuItem meal = new()
        {
            CategoryId = category.Id,
            Name = "Chips and nuggets",
            PriceCents = 600,
            IsAvailable = true,
            IsActive = true,
            AllergensConfirmed = true,
            ContainsAllergens = Allergen.CerealsContainingGluten
        };
        ctx.MenuItems.Add(meal);
        await ctx.SaveChangesAsync();

        MenuItemOptionGroup side = new()
        {
            MenuItemId = meal.Id,
            Name = "Choose your side",
            MinSelections = 1,
            MaxSelections = 1,
            IsActive = true
        };
        MenuItemOptionGroup extras = new()
        {
            MenuItemId = meal.Id,
            Name = "Extras",
            MinSelections = 0,
            MaxSelections = 1,
            IsActive = true
        };
        ctx.MenuItemOptionGroups.AddRange(side, extras);
        await ctx.SaveChangesAsync();

        MenuItemOption beans = new()
        {
            OptionGroupId = side.Id,
            Name = "Beans",
            IsAvailable = true,
            IsActive = true,
            AllergensConfirmed = true,
            ContainsAllergens = Allergen.Soybeans
        };
        MenuItemOption peas = new()
        {
            OptionGroupId = side.Id,
            Name = "Peas",
            IsAvailable = true,
            IsActive = true,
            AllergensConfirmed = true
        };
        MenuItemOption cheese = new()
        {
            OptionGroupId = extras.Id,
            Name = "Extra cheese",
            PriceDeltaCents = 50,
            IsAvailable = true,
            IsActive = true,
            AllergensConfirmed = true,
            ContainsAllergens = Allergen.Milk
        };
        ctx.MenuItemOptions.AddRange(beans, peas, cheese);

        QrTableToken table = new()
        {
            LocationId = location.Id,
            Label = "Table 4",
            Token = $"tok-{Guid.NewGuid():N}",
            IsActive = true
        };
        ctx.QrTableTokens.Add(table);
        await ctx.SaveChangesAsync();

        return new Fixture(company.Id, location.Id, table.Token, meal.Id, side.Id, beans.Id, peas.Id, cheese.Id);
    }

    private static CreateOrderRequestDto OrderFor(Fixture fixture, int quantity, params int[][] optionSets) => new()
    {
        TableToken = fixture.TableToken,
        Lines = [.. optionSets.Select(set => new CreateOrderLineDto
        {
            MenuItemId = fixture.MealId,
            Quantity = quantity,
            SelectedOptionIds = [.. set]
        })]
    };

    [TestMethod]
    public async Task CreateOrder_AddsTheChoicesPriceToTheLine()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture f = await SeedMealAsync(options);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(f, 1, [f.BeansId, f.CheeseId]), f.CompanyId);

        Assert.IsTrue(result.IsSuccess, result.Error);

        // 600 for the meal, nothing for beans, 50 for the cheese.
        Assert.AreEqual(650, result.Data!.TotalCents);
    }

    /// <summary>
    /// The kitchen has to be able to tell the two plates apart, so they must stay two lines.
    /// Collapsing them by dish id would produce one ticket line reading "2x chips and nuggets"
    /// with no way to know one of them wanted peas.
    /// </summary>
    [TestMethod]
    public async Task CreateOrder_KeepsTheSameDishWithDifferentChoicesOnSeparateLines()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture f = await SeedMealAsync(options);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(f, 1, [f.BeansId], [f.PeasId]), f.CompanyId);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        List<KitchenOrderItem> lines = await ctx.KitchenOrderItems
            .Include(i => i.Options)
            .ToListAsync();

        Assert.AreEqual(2, lines.Count);
        CollectionAssert.AreEquivalent(
            new[] { "Beans", "Peas" },
            lines.Select(l => l.Options.Single().OptionNameSnapshot).ToArray());
    }

    [TestMethod]
    public async Task CreateOrder_MergesTwoTapsOfTheIdenticalCombination()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture f = await SeedMealAsync(options);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(f, 1, [f.BeansId], [f.BeansId]), f.CompanyId);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        KitchenOrderItem line = await ctx.KitchenOrderItems.SingleAsync();

        Assert.AreEqual(2, line.Quantity);
    }

    /// <summary>
    /// The declaration on the ticket has to cover what is actually on the plate. Beans carry
    /// soybeans and the cheese carries milk; neither is on the meal itself.
    /// </summary>
    [TestMethod]
    public async Task CreateOrder_CombinesTheDishAndChoiceAllergens()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture f = await SeedMealAsync(options);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(f, 1, [f.BeansId, f.CheeseId]), f.CompanyId);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        KitchenOrderItem line = await ctx.KitchenOrderItems.SingleAsync();

        Assert.IsTrue(line.ContainsAllergensSnapshot.HasFlag(Allergen.CerealsContainingGluten));
        Assert.IsTrue(line.ContainsAllergensSnapshot.HasFlag(Allergen.Soybeans));
        Assert.IsTrue(line.ContainsAllergensSnapshot.HasFlag(Allergen.Milk));
    }

    [TestMethod]
    public async Task CreateOrder_RejectsAMissingRequiredChoice()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture f = await SeedMealAsync(options);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(f, 1, [[]]), f.CompanyId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "\"Choose your side\" is required");
    }

    [TestMethod]
    public async Task CreateOrder_RejectsMoreChoicesThanTheGroupAllows()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture f = await SeedMealAsync(options);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(f, 1, [f.BeansId, f.PeasId]), f.CompanyId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "at most");
    }

    /// <summary>
    /// A crafted request must not be able to attach another dish's cheaper option, which would
    /// otherwise be a way to reprice a meal from the client.
    /// </summary>
    [TestMethod]
    public async Task CreateOrder_RejectsAnOptionBelongingToAnotherDish()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture f = await SeedMealAsync(options);
        Fixture other = await SeedMealAsync(options);

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(f, 1, [other.BeansId]), f.CompanyId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "no longer available");
    }

    [TestMethod]
    public async Task CreateOrder_RejectsAChoiceTheKitchenHasRunOutOf()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture f = await SeedMealAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            MenuItemOption beans = await ctx.MenuItemOptions.SingleAsync(o => o.Id == f.BeansId);
            beans.IsAvailable = false;
            await ctx.SaveChangesAsync();
        }

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(f, 1, [f.BeansId]), f.CompanyId);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "run out of");
    }

    /// <summary>
    /// Same rule as an unconfirmed dish, and the same reason: a blank declaration must never be
    /// read as "contains nothing", so an unconfirmed choice cannot be ordered even by a phone
    /// holding a menu from before it was withdrawn.
    /// </summary>
    [TestMethod]
    public async Task CreateOrder_RejectsAChoiceWithNoConfirmedAllergenDeclaration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture f = await SeedMealAsync(options);

        await using (ApplicationDbContext ctx = new(options))
        {
            MenuItemOption beans = await ctx.MenuItemOptions.SingleAsync(o => o.Id == f.BeansId);
            beans.AllergensConfirmed = false;
            await ctx.SaveChangesAsync();
        }

        Result<CreateOrderResultDto> result = await GetService(options)
            .CreateOrderAsync(OrderFor(f, 1, [f.BeansId]), f.CompanyId);

        Assert.IsFalse(result.IsSuccess);
    }

    /// <summary>
    /// The choices go on the ticket, not just in the database - the kitchen reads them off the
    /// card to know what to plate.
    /// </summary>
    [TestMethod]
    public async Task Ticket_ShowsTheChosenOptions()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture f = await SeedMealAsync(options);
        KitchenOrderService service = GetService(options);

        Result<CreateOrderResultDto> created = await service
            .CreateOrderAsync(OrderFor(f, 1, [f.BeansId]), f.CompanyId);

        Assert.IsTrue(created.IsSuccess, created.Error);

        Result<OrderStatusDto> status = await service
            .GetOrderStatusAsync(created.Data!.OrderToken, f.CompanyId);

        Assert.IsTrue(status.IsSuccess, status.Error);
        Assert.AreEqual("Beans", status.Data!.Lines.Single().Options.Single().OptionName);
        Assert.AreEqual("Choose your side", status.Data.Lines.Single().Options.Single().GroupName);
    }
}
