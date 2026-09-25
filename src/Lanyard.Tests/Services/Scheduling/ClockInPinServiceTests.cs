using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class ClockInPinServiceTests
{
    private static ClockInPinService GetService(DbContextOptions<ApplicationDbContext> options) =>
        new(SchedulingTestHelpers.GetFactory(options), new PasswordHasher<UserProfile>());

    [TestMethod]
    [DataRow("123")]
    [DataRow("1234567")]
    [DataRow("12a4")]
    [DataRow("")]
    [DataRow("   ")]
    public async Task SetPinAsync_RejectsInvalidFormat(string pin)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);

        Result<bool> result = await GetService(options).SetOwnPinAsync(user.Id, pin);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SetPinAsync_StoresHashNotPin()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);

        Result<bool> result = await GetService(options).SetPinForUserAsync(SchedulingTestHelpers.AdminScope, user.Id, "1234", "manager-id");

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        UserClockInPin saved = await ctx.UserClockInPins.SingleAsync();
        Assert.AreNotEqual("1234", saved.PinHash);
        Assert.IsFalse(saved.PinHash.Contains("1234"));
        Assert.AreEqual("manager-id", saved.SetByUserId);
    }

    [TestMethod]
    public async Task SetPinAsync_ReplacesExistingPin()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        ClockInPinService service = GetService(options);

        await service.SetOwnPinAsync(user.Id, "1234");
        await service.SetOwnPinAsync(user.Id, "5678");

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.UserClockInPins.CountAsync());
        Assert.IsTrue((await service.VerifyPinAsync(user.Id, "5678")).Data);
        Assert.IsFalse((await service.VerifyPinAsync(user.Id, "1234")).Data);
    }

    [TestMethod]
    public async Task SetPinAsync_FailsForUnknownUser()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();

        Result<bool> result = await GetService(options).SetOwnPinAsync("nobody", "1234");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task VerifyPinAsync_ReturnsTrueForCorrectPin()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        ClockInPinService service = GetService(options);
        await service.SetOwnPinAsync(user.Id, "246810");

        Result<bool> result = await service.VerifyPinAsync(user.Id, "246810");

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Data);
    }

    [TestMethod]
    public async Task VerifyPinAsync_ReturnsFalseForWrongPinAndForNoPin()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile withPin = await SchedulingTestHelpers.SeedUserAsync(options, location, "Ann");
        UserProfile withoutPin = await SchedulingTestHelpers.SeedUserAsync(options, location, "Bob");
        ClockInPinService service = GetService(options);
        await service.SetOwnPinAsync(withPin.Id, "1234");

        Result<bool> wrong = await service.VerifyPinAsync(withPin.Id, "9999");
        Result<bool> none = await service.VerifyPinAsync(withoutPin.Id, "1234");

        Assert.IsTrue(wrong.IsSuccess);
        Assert.IsFalse(wrong.Data);
        Assert.IsTrue(none.IsSuccess);
        Assert.IsFalse(none.Data);
    }

    [TestMethod]
    public async Task ClearPinAsync_RemovesRowAndIsIdempotent()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        ClockInPinService service = GetService(options);
        await service.SetOwnPinAsync(user.Id, "1234");

        Result<bool> first = await service.ClearOwnPinAsync(user.Id);
        Result<bool> second = await service.ClearOwnPinAsync(user.Id);

        Assert.IsTrue(first.IsSuccess);
        Assert.IsTrue(second.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.UserClockInPins.CountAsync());
    }

    [TestMethod]
    public async Task GetStatusAsync_ReportsWhetherPinExists()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        ClockInPinService service = GetService(options);

        Result<ClockInPinStatus> before = await service.GetStatusAsync(user.Id);
        await service.SetOwnPinAsync(user.Id, "1234");
        Result<ClockInPinStatus> after = await service.GetStatusAsync(user.Id);

        Assert.IsFalse(before.Data!.HasPin);
        Assert.IsNull(before.Data.SetDate);
        Assert.IsTrue(after.Data!.HasPin);
        Assert.IsNotNull(after.Data.SetDate);
    }
    [TestMethod]
    public async Task SetPinForUserAsync_RejectsManagerFromOtherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);

        Result<bool> result = await GetService(options).SetPinForUserAsync(
            SchedulingTestHelpers.ManagerScopeFor(otherLocation), user.Id, "1234", "other-manager");

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(0, await ctx.UserClockInPins.CountAsync());
    }

    [TestMethod]
    public async Task SetPinForUserAsync_AllowsManagerOfUsersCompanyAndRecordsSetBy()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);

        Result<bool> result = await GetService(options).SetPinForUserAsync(
            SchedulingTestHelpers.ManagerScopeFor(location), user.Id, "1234", "manager-id");

        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual("manager-id", (await ctx.UserClockInPins.SingleAsync()).SetByUserId);
    }

    [TestMethod]
    public async Task ClearPinForUserAsync_RejectsManagerFromOtherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        (_, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        ClockInPinService service = GetService(options);
        await service.SetOwnPinAsync(user.Id, "1234");

        Result<bool> result = await service.ClearPinForUserAsync(SchedulingTestHelpers.ManagerScopeFor(otherLocation), user.Id);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsTrue((await service.VerifyPinAsync(user.Id, "1234")).Data);
    }

    [TestMethod]
    public async Task SetOwnPinAsync_ClearsSetByUserId()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile user = await SchedulingTestHelpers.SeedUserAsync(options, location);
        ClockInPinService service = GetService(options);
        await service.SetPinForUserAsync(SchedulingTestHelpers.AdminScope, user.Id, "1234", "manager-id");

        await service.SetOwnPinAsync(user.Id, "5678");

        await using ApplicationDbContext ctx = new(options);
        Assert.IsNull((await ctx.UserClockInPins.SingleAsync()).SetByUserId);
    }

    [TestMethod]
    public async Task SetPinForUserAsync_RejectsStaffWithoutManagerRole()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        UserProfile colleague = await SchedulingTestHelpers.SeedUserAsync(options, location, "Colleague");

        Result<bool> result = await GetService(options).SetPinForUserAsync(
            SchedulingTestHelpers.StaffScopeFor(location), colleague.Id, "1234", "nosy-coworker");

        Assert.IsFalse(result.IsSuccess);
    }
}
