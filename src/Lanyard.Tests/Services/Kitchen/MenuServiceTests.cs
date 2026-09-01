using Lanyard.Application.Services.Kitchen;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Kitchen;

[TestClass]
public class MenuServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static MenuService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new MenuService(factoryMock.Object, new Mock<ILogger<MenuService>>().Object);
    }

    private sealed record Fixture(int CompanyId, int LocationId, int CategoryId, int BurgerId);

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
        ctx.MenuItems.Add(burger);
        await ctx.SaveChangesAsync();

        return new Fixture(company.Id, location.Id, category.Id, burger.Id);
    }

    [TestMethod]
    public async Task GetPublicMenuAsync_ReturnsActiveCategoriesAndItems()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);

        Result<MenuDto> result = await GetService(options).GetPublicMenuAsync(fixture.LocationId, fixture.CompanyId);

        Assert.IsTrue(result.Success, result.Error);
        MenuCategoryDto category = result.Data!.Categories.Single();
        Assert.AreEqual("Mains", category.Name);
        Assert.AreEqual("Burger", category.Items.Single().Name);
    }

    /// <summary>
    /// Sold-out items stay on the menu flagged unavailable rather than vanishing: a dish that
    /// disappears mid-browse reads as a broken menu, and removing it reshuffles the list under
    /// whoever is looking at it.
    /// </summary>
    [TestMethod]
    public async Task GetPublicMenuAsync_KeepsUnavailableItemsVisibleButFlagged()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        MenuService service = GetService(options);

        await service.SetItemAvailabilityAsync(fixture.BurgerId, false);

        Result<MenuDto> result = await service.GetPublicMenuAsync(fixture.LocationId, fixture.CompanyId);

        MenuItemDto item = result.Data!.Categories.Single().Items.Single();
        Assert.AreEqual("Burger", item.Name);
        Assert.IsFalse(item.IsAvailable);
    }

    [TestMethod]
    public async Task GetPublicMenuAsync_HidesDeactivatedItems()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        MenuService service = GetService(options);

        await service.DeactivateItemAsync(fixture.BurgerId);

        Result<MenuDto> result = await service.GetPublicMenuAsync(fixture.LocationId, fixture.CompanyId);

        Assert.AreEqual(0, result.Data!.Categories.Single().Items.Count);
    }

    [TestMethod]
    public async Task GetPublicMenuAsync_RefusesAnotherCompanysLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture play2Day = await SeedVenueAsync(options);
        Fixture partyman = await SeedVenueAsync(options, companyName: "Partyman");

        Result<MenuDto> result = await GetService(options).GetPublicMenuAsync(play2Day.LocationId, partyman.CompanyId);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Location not found.", result.Error);
    }

    [TestMethod]
    public async Task GetPublicMenuAsync_RefusesWhenOrderingIsDisabled()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options, orderingEnabled: false);

        Result<MenuDto> result = await GetService(options).GetPublicMenuAsync(fixture.LocationId, fixture.CompanyId);

        Assert.IsFalse(result.Success);
    }

    /// <summary>
    /// The version bump is the entire mechanism by which 86'ing an item reaches a phone that is
    /// already browsing, so it has to move on an availability change specifically.
    /// </summary>
    [TestMethod]
    public async Task SetItemAvailabilityAsync_AdvancesTheLocationsMenuVersion()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        MenuService service = GetService(options);

        DateTime before = (await service.GetPublicMenuAsync(fixture.LocationId, fixture.CompanyId)).Data!.MenuVersion;

        await service.SetItemAvailabilityAsync(fixture.BurgerId, false);

        DateTime after = (await service.GetPublicMenuAsync(fixture.LocationId, fixture.CompanyId)).Data!.MenuVersion;

        Assert.IsTrue(after > before, "Menu version should advance when an item is marked unavailable.");
    }

    [TestMethod]
    public async Task SaveItemAsync_AdvancesTheLocationsMenuVersion()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);
        MenuService service = GetService(options);

        DateTime before = (await service.GetPublicMenuAsync(fixture.LocationId, fixture.CompanyId)).Data!.MenuVersion;

        await service.SaveItemAsync(new MenuItem
        {
            CategoryId = fixture.CategoryId,
            Name = "Wings",
            PriceCents = 600,
            IsAvailable = true
        });

        DateTime after = (await service.GetPublicMenuAsync(fixture.LocationId, fixture.CompanyId)).Data!.MenuVersion;

        Assert.IsTrue(after > before, "Menu version should advance when an item is added.");
    }

    [TestMethod]
    public async Task SaveItemAsync_RejectsNegativePrice()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture fixture = await SeedVenueAsync(options);

        Result<MenuItem> result = await GetService(options).SaveItemAsync(new MenuItem
        {
            CategoryId = fixture.CategoryId,
            Name = "Free lunch",
            PriceCents = -100
        });

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task GetItemImageFileIdAsync_RefusesAnotherCompanysItem()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Fixture play2Day = await SeedVenueAsync(options);
        Fixture partyman = await SeedVenueAsync(options, companyName: "Partyman");

        Result<Guid> result = await GetService(options).GetItemImageFileIdAsync(play2Day.BurgerId, partyman.CompanyId);

        Assert.IsFalse(result.Success);
    }
}
