using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Parties;

[TestClass]
public class PartySettingsServiceTests
{
    private const string ActingUser = "acting-user";

    private static PartySettingsService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options));

    private static async Task<PartyMenuItem> AddItemAsync(DbContextOptions<ApplicationDbContext> options, Location location,
        PartyMenuItemKind kind, string name)
    {
        Result<PartyMenuItem> result = await GetService(options).SaveMenuItemAsync(SchedulingTestHelpers.AdminScope,
            new PartyMenuItem { LocationId = location.Id, Kind = kind, Name = name }, ActingUser);

        Assert.IsTrue(result.IsSuccess, result.Error);

        return result.Data!;
    }

    [TestMethod]
    public async Task GetSettingsAsync_ReturnsDefaultsWhenNoneSaved()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<PartyLocationSettings> result = await GetService(options).GetSettingsAsync(SchedulingTestHelpers.ManagerScopeFor(location), location.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(Guid.Empty, result.Data!.Id);
        Assert.AreEqual(120, result.Data.DefaultLengthMinutes);
        Assert.IsNull(result.Data.EatTimeOffsetMinutes);
        Assert.IsNull(result.Data.LaserTagOffsetMinutes);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_CreatesThenUpdatesOneRowPerLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        PartySettingsService service = GetService(options);

        await service.SaveSettingsAsync(SchedulingTestHelpers.ManagerScopeFor(location),
            new PartyLocationSettings { LocationId = location.Id, DefaultLengthMinutes = 120, EatTimeOffsetMinutes = 60 }, ActingUser);
        Result<PartyLocationSettings> second = await service.SaveSettingsAsync(SchedulingTestHelpers.ManagerScopeFor(location),
            new PartyLocationSettings { LocationId = location.Id, DefaultLengthMinutes = 150, EatTimeOffsetMinutes = 75, LaserTagOffsetMinutes = 30 }, ActingUser);

        Assert.IsTrue(second.IsSuccess, second.Error);

        await using ApplicationDbContext ctx = new(options);
        PartyLocationSettings saved = await ctx.PartyLocationSettings.SingleAsync();
        Assert.AreEqual(150, saved.DefaultLengthMinutes);
        Assert.AreEqual(75, saved.EatTimeOffsetMinutes);
        Assert.AreEqual(30, saved.LaserTagOffsetMinutes);
        Assert.AreEqual(ActingUser, saved.UpdatedByUserId);
    }

    [TestMethod]
    [DataRow(10, null, null, "between 15 minutes")]
    [DataRow(120, 150, null, "eating time")]
    [DataRow(120, null, -5, "laser tag time")]
    public async Task SaveSettingsAsync_RejectsTimingsOutsideTheParty(int length, int? eat, int? laser, string expected)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<PartyLocationSettings> result = await GetService(options).SaveSettingsAsync(SchedulingTestHelpers.AdminScope,
            new PartyLocationSettings { LocationId = location.Id, DefaultLengthMinutes = length, EatTimeOffsetMinutes = eat, LaserTagOffsetMinutes = laser },
            ActingUser);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, expected);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_RejectsManagerAtAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Result<PartyLocationSettings> result = await GetService(options).SaveSettingsAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation),
            new PartyLocationSettings { LocationId = location.Id }, ActingUser);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveMenuItemAsync_AddsToTheEndOfItsKindAndTrimsText()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        await AddItemAsync(options, location, PartyMenuItemKind.HotMain, "Sausage");
        await AddItemAsync(options, location, PartyMenuItemKind.HotSide, "Peas");

        Result<PartyMenuItem> result = await GetService(options).SaveMenuItemAsync(SchedulingTestHelpers.ManagerScopeFor(location),
            new PartyMenuItem
            {
                LocationId = location.Id,
                Kind = PartyMenuItemKind.HotMain,
                Name = "  Nuggets ",
                Description = " with chips ",
                AllergenText = "  "
            }, ActingUser);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Nuggets", result.Data!.Name);
        Assert.AreEqual("with chips", result.Data.Description);
        Assert.IsNull(result.Data.AllergenText);
        Assert.AreEqual(1, result.Data.SortOrder, "Sorted after Sausage, not counting the side.");
    }

    [TestMethod]
    public async Task SaveMenuItemAsync_RequiresAName()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<PartyMenuItem> result = await GetService(options).SaveMenuItemAsync(SchedulingTestHelpers.AdminScope,
            new PartyMenuItem { LocationId = location.Id, Kind = PartyMenuItemKind.Sandwich, Name = " " }, ActingUser);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveMenuItemAsync_UpdatesTheText()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        PartyMenuItem item = await AddItemAsync(options, location, PartyMenuItemKind.Sandwich, "Ham");

        Result<PartyMenuItem> result = await GetService(options).SaveMenuItemAsync(SchedulingTestHelpers.AdminScope,
            new PartyMenuItem { Id = item.Id, LocationId = location.Id, Kind = PartyMenuItemKind.Sandwich, Name = "Ham", AllergenText = "Contains: Sulphites" },
            ActingUser);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual("Contains: Sulphites", (await ctx.PartyMenuItems.SingleAsync()).AllergenText);
    }

    [TestMethod]
    public async Task SaveMenuItemAsync_RejectsManagerAtAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Result<PartyMenuItem> result = await GetService(options).SaveMenuItemAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation),
            new PartyMenuItem { LocationId = location.Id, Kind = PartyMenuItemKind.Sandwich, Name = "Ham" }, ActingUser);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetMenuAsync_ReturnsActiveItemsByKindThenOrder()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        await AddItemAsync(options, location, PartyMenuItemKind.Sandwich, "Ham");
        await AddItemAsync(options, location, PartyMenuItemKind.HotMain, "Sausage");
        await AddItemAsync(options, location, PartyMenuItemKind.HotMain, "Burger");
        PartyMenuItem removed = await AddItemAsync(options, location, PartyMenuItemKind.HotMain, "Retired");
        await AddItemAsync(options, otherLocation, PartyMenuItemKind.HotMain, "Elsewhere");

        PartySettingsService service = GetService(options);
        await service.RemoveMenuItemAsync(SchedulingTestHelpers.AdminScope, removed.Id, ActingUser);

        Result<List<PartyMenuItem>> result = await service.GetMenuAsync(SchedulingTestHelpers.ManagerScopeFor(location), location.Id);

        CollectionAssert.AreEqual(new[] { "Sausage", "Burger", "Ham" }, result.Data!.Select(x => x.Name).ToArray());
    }

    [TestMethod]
    public async Task MoveMenuItemAsync_SwapsWithNeighbourOfSameKind()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        await AddItemAsync(options, location, PartyMenuItemKind.HotMain, "Sausage");
        await AddItemAsync(options, location, PartyMenuItemKind.HotMain, "Burger");
        PartyMenuItem nuggets = await AddItemAsync(options, location, PartyMenuItemKind.HotMain, "Nuggets");
        PartySettingsService service = GetService(options);

        Result<bool> moved = await service.MoveMenuItemAsync(SchedulingTestHelpers.ManagerScopeFor(location), nuggets.Id, -1);
        Result<bool> atTop = await service.MoveMenuItemAsync(SchedulingTestHelpers.ManagerScopeFor(location), nuggets.Id, -1);
        Result<bool> pastTop = await service.MoveMenuItemAsync(SchedulingTestHelpers.ManagerScopeFor(location), nuggets.Id, -1);

        Assert.IsTrue(moved.Data);
        Assert.IsTrue(atTop.Data);
        Assert.IsFalse(pastTop.Data, "Already first - nothing to swap with.");

        Result<List<PartyMenuItem>> menu = await service.GetMenuAsync(SchedulingTestHelpers.AdminScope, location.Id);
        CollectionAssert.AreEqual(new[] { "Nuggets", "Sausage", "Burger" }, menu.Data!.Select(x => x.Name).ToArray());
    }

    [TestMethod]
    public async Task RemoveAndMove_RejectManagerAtAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        await AddItemAsync(options, location, PartyMenuItemKind.HotMain, "Sausage");
        PartyMenuItem burger = await AddItemAsync(options, location, PartyMenuItemKind.HotMain, "Burger");
        PartySettingsService service = GetService(options);

        Result<bool> removed = await service.RemoveMenuItemAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation), burger.Id, ActingUser);
        Result<bool> moved = await service.MoveMenuItemAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation), burger.Id, -1);

        Assert.IsFalse(removed.IsSuccess);
        Assert.IsFalse(moved.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.IsTrue((await ctx.PartyMenuItems.SingleAsync(x => x.Id == burger.Id)).IsActive);
    }
}
