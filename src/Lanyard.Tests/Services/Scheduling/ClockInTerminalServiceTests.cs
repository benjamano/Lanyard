using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class ClockInTerminalServiceTests
{
    private static ClockInTerminalService GetService(DbContextOptions<ApplicationDbContext> options, TimeProvider? clock = null) =>
        new(SchedulingTestHelpers.GetFactory(options), clock ?? TimeProvider.System, NullLogger<ClockInTerminalService>.Instance);

    [TestMethod]
    public async Task PairAsync_StoresOnlyAHashOfTheToken_AndResolveFindsTheTerminal()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        ClockInTerminalService service = GetService(options);

        Result<PairedTerminal> paired = await service.PairAsync(location.Id, "  Front desk  ", "manager");

        Assert.IsTrue(paired.IsSuccess, paired.Error);
        string raw = paired.Data!.RawToken;
        Assert.IsTrue(raw.Length >= 40);

        await using (ApplicationDbContext ctx = new(options))
        {
            ClockInTerminal stored = await ctx.ClockInTerminals.SingleAsync();
            Assert.AreEqual("Front desk", stored.Name);
            Assert.AreNotEqual(raw, stored.DeviceTokenHash);
            Assert.IsFalse(stored.DeviceTokenHash.Contains(raw));
        }

        Result<TerminalSession> session = await service.ResolveByTokenAsync(raw);

        Assert.IsTrue(session.IsSuccess, session.Error);
        Assert.AreEqual(location.Id, session.Data!.LocationId);
        Assert.AreEqual(location.Name, session.Data.LocationName);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not-a-real-token")]
    public async Task ResolveByTokenAsync_FailsForMissingOrUnknownToken(string? token)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();

        Result<TerminalSession> result = await GetService(options).ResolveByTokenAsync(token);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ResolveByTokenAsync_FailsOnceRevoked()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        ClockInTerminalService service = GetService(options);
        Result<PairedTerminal> paired = await service.PairAsync(location.Id, "Front desk", "manager");

        Result<bool> revoked = await service.RevokeAsync(SchedulingTestHelpers.ManagerScopeFor(location), paired.Data!.Terminal.Id, "manager");

        Assert.IsTrue(revoked.IsSuccess);
        Assert.IsFalse((await service.ResolveByTokenAsync(paired.Data.RawToken)).IsSuccess);
        Assert.IsFalse((await service.GetActiveSessionAsync(paired.Data.Terminal.Id)).IsSuccess);
    }

    [TestMethod]
    public async Task ResolveByTokenAsync_UpdatesLastSeenAtMostEveryFiveMinutes()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        TestClock clock = new(new DateTime(2026, 10, 5, 9, 0, 0));
        ClockInTerminalService service = GetService(options, clock);
        Result<PairedTerminal> paired = await service.PairAsync(location.Id, "Front desk", "manager");

        async Task<DateTime?> LastSeenAsync()
        {
            await using ApplicationDbContext ctx = new(options);
            return (await ctx.ClockInTerminals.SingleAsync()).LastSeenUtc;
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        await service.ResolveByTokenAsync(paired.Data!.RawToken);
        Assert.AreEqual(new DateTime(2026, 10, 5, 9, 0, 0), await LastSeenAsync());

        clock.Advance(TimeSpan.FromMinutes(4));
        await service.ResolveByTokenAsync(paired.Data.RawToken);
        Assert.AreEqual(new DateTime(2026, 10, 5, 9, 6, 0), await LastSeenAsync());
    }

    [TestMethod]
    public async Task GetActiveSessionAsync_AlsoUpdatesLastSeen_SoAWallTabletStaysCurrent()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        TestClock clock = new(new DateTime(2026, 10, 5, 9, 0, 0));
        ClockInTerminalService service = GetService(options, clock);
        Result<PairedTerminal> paired = await service.PairAsync(location.Id, "Front desk", "manager");

        // The page was loaded once; after that only its regular check runs.
        clock.Advance(TimeSpan.FromDays(3));
        Assert.IsTrue((await service.GetActiveSessionAsync(paired.Data!.Terminal.Id)).IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(new DateTime(2026, 10, 8, 9, 0, 0), (await ctx.ClockInTerminals.SingleAsync()).LastSeenUtc);
    }

    [TestMethod]
    public async Task PairAsync_RejectsBlankName()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (_, Location location) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Result<PairedTerminal> result = await GetService(options).PairAsync(location.Id, "   ", "manager");

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task GetTerminalsAsync_ManagerSeesOnlyTheirLocation_StaffSeesNothing()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        Location wisbech;

        await using (ApplicationDbContext ctx = new(options))
        {
            wisbech = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(wisbech);
            await ctx.SaveChangesAsync();
        }

        await SchedulingTestHelpers.SeedTerminalAsync(options, ipswich, "Ipswich tablet");
        await SchedulingTestHelpers.SeedTerminalAsync(options, wisbech, "Wisbech tablet");
        ClockInTerminalService service = GetService(options);

        Result<List<ClockInTerminal>> manager = await service.GetTerminalsAsync(SchedulingTestHelpers.ManagerScopeFor(ipswich));
        Result<List<ClockInTerminal>> admin = await service.GetTerminalsAsync(SchedulingTestHelpers.AdminScope);
        Result<List<ClockInTerminal>> staff = await service.GetTerminalsAsync(SchedulingTestHelpers.StaffScopeFor(ipswich));

        Assert.AreEqual("Ipswich tablet", manager.Data!.Single().Name);
        Assert.AreEqual(2, admin.Data!.Count);
        Assert.IsFalse(staff.IsSuccess);
    }

    [TestMethod]
    public async Task RevokeAsync_RejectsManagerOfAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);
        ClockInTerminal terminal = await SchedulingTestHelpers.SeedTerminalAsync(options, ipswich);
        Location wisbech;

        await using (ApplicationDbContext ctx = new(options))
        {
            wisbech = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(wisbech);
            await ctx.SaveChangesAsync();
        }

        Result<bool> result = await GetService(options).RevokeAsync(SchedulingTestHelpers.ManagerScopeFor(wisbech), terminal.Id, "other-manager");

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext verify = new(options);
        Assert.IsTrue((await verify.ClockInTerminals.SingleAsync()).IsActive);
    }
}
