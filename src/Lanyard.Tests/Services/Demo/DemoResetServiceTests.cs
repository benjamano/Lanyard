using Lanyard.Application.Services.Demo;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Demo;

// The nightly demo reset against a real relational database (SQLite: EF InMemory can't run
// ExecuteDelete, and SQLite enforces the foreign keys the wipe has to respect). The thing that
// matters most: it must never touch a real company's rows.
[TestClass]
public class DemoResetServiceTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<ApplicationDbContext> _options = null!;
    private ServiceProvider _provider = null!;

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Lanyard.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [TestInitialize]
    public async Task Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        await using (ApplicationDbContext ctx = new(_options))
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        ServiceCollection services = new();
        services.AddSingleton(_options);
        services.AddScoped(sp => new ApplicationDbContext(sp.GetRequiredService<DbContextOptions<ApplicationDbContext>>()));
        services.AddDataProtection();
        services.AddLogging();
        services.AddIdentityCore<UserProfile>(o => o.Password.RequireNonAlphanumeric = false)
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        _provider = services.BuildServiceProvider();

        // Play2Day, its locations, the seed admin and the standard roles - a real customer's data
        // for the reset to leave alone.
        await DatabaseSeeder.SeedAsync(_provider);

        await using (ApplicationDbContext ctx = new(_options))
        {
            ctx.Songs.Add(new Song { Id = Guid.NewGuid(), Name = "Real song", AlbumName = "Real", FilePath = "real.mp3", CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId, IsActive = true });
            ctx.Announcements.Add(new Announcement { Id = Guid.NewGuid(), CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId, LocationId = ApplicationDbContext.SeedIpswichLocationId, Title = "Real announcement", Body = "Real", IsActive = true });
            await ctx.SaveChangesAsync();
        }
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private DemoResetService BuildService(bool enabled = true) => new(
        _options,
        _provider.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new DemoOptions { Enabled = enabled }),
        new DemoDirectory(new SystemDbContextFactory(_options), TimeProvider.System),
        TimeProvider.System,
        NullLogger<DemoResetService>.Instance);

    private async Task<int> DemoCompanyIdAsync()
    {
        await using ApplicationDbContext ctx = new(_options);
        return await ctx.Companies.Where(x => x.IsDemo).Select(x => x.Id).SingleAsync();
    }

    [TestMethod]
    public async Task FirstReset_CreatesAndFillsTheDemoCompany()
    {
        Result<bool> result = await BuildService().ResetAsync();

        Assert.IsTrue(result.IsSuccess, result.Error);

        int demoId = await DemoCompanyIdAsync();
        await using ApplicationDbContext demo = new(_options, new FixedTenantProvider(demoId));

        Assert.AreEqual(2, await demo.Locations.CountAsync(x => x.CompanyId == demoId));
        Assert.IsTrue(await demo.Shifts.AnyAsync(x => x.PublishedDateUtc != null && x.UserId == DemoAccounts.StaffUserId));
        Assert.IsTrue(await demo.Courses.AnyAsync());
        Assert.IsTrue(await demo.ChatMessages.AnyAsync());
        Assert.IsTrue(await demo.Dashboards.AnyAsync());
        Assert.IsTrue(await demo.AppSettings.AnyAsync(x => x.Key == "Dashboard.OrganisationDefaultDashboardId"));

        foreach (string userId in DemoAccounts.LoginUserIds)
        {
            Assert.IsTrue(await demo.UserLocationMemberships.AnyAsync(m => m.UserId == userId && m.Location!.CompanyId == demoId), userId);
        }

        Assert.IsNotNull(await BuildService().GetLastResetUtcAsync());
    }

    [TestMethod]
    public async Task Reset_PutsVisitorsChangesBack_AndNeverTouchesARealCompany()
    {
        DemoResetService service = BuildService();
        Assert.IsTrue((await service.ResetAsync()).IsSuccess);
        int demoId = await DemoCompanyIdAsync();

        int demoAnnouncementsAfterSeed;
        await using (ApplicationDbContext ctx = new(_options))
        {
            demoAnnouncementsAfterSeed = await ctx.Announcements.CountAsync(x => x.CompanyId == demoId);

            // What a visitor might do: rename the company, post, and invite someone.
            Company demo = await ctx.Companies.SingleAsync(x => x.Id == demoId);
            demo.Name = "Renamed by a visitor";
            int demoLocationId = await ctx.Locations.Where(x => x.CompanyId == demoId).Select(x => x.Id).FirstAsync();
            ctx.Announcements.Add(new Announcement { Id = Guid.NewGuid(), CompanyId = demoId, LocationId = demoLocationId, Title = "Visitor post", Body = "hi", IsActive = true });
            ctx.Users.Add(new UserProfile { Id = "visitor-made", UserName = "visitor", NormalizedUserName = "VISITOR", SecurityStamp = "x" });
            ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = "visitor-made", LocationId = demoLocationId });
            await ctx.SaveChangesAsync();
        }

        Result<bool> result = await service.ResetAsync();
        Assert.IsTrue(result.IsSuccess, result.Error);

        await using (ApplicationDbContext ctx = new(_options))
        {
            Assert.AreEqual(DemoSeeder.CompanyName, (await ctx.Companies.SingleAsync(x => x.Id == demoId)).Name);
            Assert.AreEqual(demoAnnouncementsAfterSeed, await ctx.Announcements.CountAsync(x => x.CompanyId == demoId));
            Assert.IsFalse(await ctx.Users.AnyAsync(x => x.Id == "visitor-made"));

            // Play2Day's rows, users and locations are exactly as they were.
            Assert.IsTrue(await ctx.Songs.AnyAsync(x => x.Name == "Real song" && x.CompanyId == ApplicationDbContext.SeedPlay2DayCompanyId));
            Assert.IsTrue(await ctx.Announcements.AnyAsync(x => x.Title == "Real announcement"));
            Assert.IsTrue(await ctx.Users.AnyAsync(x => x.Id == ApplicationDbContext.SeedAdminUserId));
            Assert.AreEqual(2, await ctx.Locations.CountAsync(x => x.CompanyId == ApplicationDbContext.SeedPlay2DayCompanyId));
            Assert.AreEqual("Play2Day", (await ctx.Companies.SingleAsync(x => x.Id == ApplicationDbContext.SeedPlay2DayCompanyId)).Name);
        }
    }

    [TestMethod]
    public async Task Reset_KeepsTheDemoLoginAccountsSoOpenSessionsSurvive()
    {
        DemoResetService service = BuildService();
        Assert.IsTrue((await service.ResetAsync()).IsSuccess);

        string? stampBefore;
        await using (ApplicationDbContext ctx = new(_options))
        {
            stampBefore = (await ctx.Users.SingleAsync(x => x.Id == DemoAccounts.StaffUserId)).SecurityStamp;
        }

        Assert.IsTrue((await service.ResetAsync()).IsSuccess);

        await using (ApplicationDbContext ctx = new(_options))
        {
            UserProfile staff = await ctx.Users.SingleAsync(x => x.Id == DemoAccounts.StaffUserId);

            // A changed security stamp would sign every visitor out at the next revalidation.
            Assert.AreEqual(stampBefore, staff.SecurityStamp);
            Assert.AreEqual(1, await ctx.Companies.CountAsync(x => x.IsDemo));
        }
    }

    [TestMethod]
    public async Task Reset_RefusesWhenTheDemoIsDisabled()
    {
        Result<bool> result = await BuildService(enabled: false).ResetAsync();

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(_options);
        Assert.IsFalse(await ctx.Companies.AnyAsync(x => x.IsDemo));
    }

    [TestMethod]
    public void IsResetDue_FollowsTheDailyUkResetTime()
    {
        TimeOnly fourAm = new(4, 0);

        // 28 Sept 2026 is BST, so 04:00 UK is 03:00 UTC.
        DateTime beforeReset = new(2026, 9, 28, 2, 30, 0, DateTimeKind.Utc);
        DateTime afterReset = new(2026, 9, 28, 3, 5, 0, DateTimeKind.Utc);
        DateTime yesterdaysReset = new(2026, 9, 27, 3, 0, 0, DateTimeKind.Utc);
        DateTime todaysReset = new(2026, 9, 28, 3, 0, 30, DateTimeKind.Utc);

        Assert.IsTrue(DemoResetHostedService.IsResetDue(null, beforeReset, fourAm), "Never seeded - seed straight away.");
        Assert.IsFalse(DemoResetHostedService.IsResetDue(yesterdaysReset, beforeReset, fourAm), "Today's reset time hasn't come yet.");
        Assert.IsTrue(DemoResetHostedService.IsResetDue(yesterdaysReset, afterReset, fourAm), "Today's reset time has passed.");
        Assert.IsFalse(DemoResetHostedService.IsResetDue(todaysReset, afterReset, fourAm), "Already reset today.");
    }
}
