using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Dashboards;

[TestClass]
public class DashboardServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static DashboardService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new DashboardService(factoryMock.Object);
    }

    // Seeds a bare dashboard directly in the DbContext and returns it.
    private static async Task<Dashboard> SeedDashboardAsync(DbContextOptions<ApplicationDbContext> options, string name = "Test")
    {
        Dashboard dashboard = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsActive = true,
            CreateDate = DateTime.UtcNow
        };
        await using ApplicationDbContext ctx = new(options);
        ctx.Dashboards.Add(dashboard);
        await ctx.SaveChangesAsync();
        return dashboard;
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_CreatesDashboardWithWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard seeded = await SeedDashboardAsync(options, "Ops Dashboard");

        seeded.Widgets = [new DigitalClockWidget { GridW = 4, GridH = 3, IsActive = true }];

        Result<bool> result = await service.SaveDashboardAsync(seeded);

        Assert.IsTrue(result.Success, result.Error);

        Result<Dashboard> getResult = await service.GetDashboardAsync(seeded.Id);
        Assert.IsTrue(getResult.Success);
        Assert.HasCount(1, getResult.Data!.Widgets);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesAndSoftDeletesRemovedWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard seeded = await SeedDashboardAsync(options);

        DashboardWidget widget1 = new DigitalClockWidget { Id = Guid.NewGuid(), GridX = 0, GridY = 0, GridW = 4, GridH = 3, IsActive = true };
        DashboardWidget widget2 = new TextAreaWidget { Id = Guid.NewGuid(), GridX = 4, GridY = 0, GridW = 4, GridH = 3, IsActive = true };
        seeded.Widgets = [widget1, widget2];
        await service.SaveDashboardAsync(seeded);

        // Remove widget2 — it should be soft-deleted (IsActive = false) in the database.
        seeded.Widgets = [widget1];
        Result<bool> updateResult = await service.SaveDashboardAsync(seeded);

        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        Dashboard dbDashboard = await ctx.Dashboards.Include(x => x.Widgets).FirstAsync(x => x.Id == seeded.Id);
        Assert.HasCount(2, dbDashboard.Widgets);
        Assert.AreEqual(1, dbDashboard.Widgets.Count(x => x.IsActive));
        Assert.AreEqual(1, dbDashboard.Widgets.Count(x => x.IsActive == false));
    }

    [TestMethod]
    public async Task DashboardService_DeleteDashboard_SetsInactiveOnDashboardAndWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard seeded = await SeedDashboardAsync(options, "Delete Me");

        seeded.Widgets = [new DigitalClockWidget { GridW = 4, GridH = 3, IsActive = true }];
        await service.SaveDashboardAsync(seeded);

        Result<bool> deleteResult = await service.DeleteDashboardAsync(seeded.Id);

        Assert.IsTrue(deleteResult.Success, deleteResult.Error);

        await using ApplicationDbContext ctx = new(options);
        Dashboard dashboard = await ctx.Dashboards.Include(x => x.Widgets).FirstAsync(x => x.Id == seeded.Id);
        Assert.IsFalse(dashboard.IsActive);
        Assert.IsTrue(dashboard.Widgets.All(x => x.IsActive == false));
    }

    [TestMethod]
    public async Task DashboardService_GetDashboard_ReturnsAllWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard seeded = await SeedDashboardAsync(options, "Widget Test");

        DashboardWidget widget = new DigitalClockWidget { Id = Guid.NewGuid(), GridW = 4, GridH = 3, IsActive = true };
        seeded.Widgets = [widget];
        await service.SaveDashboardAsync(seeded);

        // Soft-delete by saving with empty widget list.
        seeded.Widgets = [];
        await service.SaveDashboardAsync(seeded);

        Result<Dashboard> getResult = await service.GetDashboardAsync(seeded.Id);

        Assert.IsTrue(getResult.Success, getResult.Error);
        Assert.AreEqual(0, getResult.Data!.Widgets.Count(x => x.IsActive));
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_FailsOnUnknownWidgetType()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard seeded = await SeedDashboardAsync(options, "Unknown Widget");

        // A base DashboardWidget with Type=Unknown is not handled by CreateWidgetCopy.
        seeded.Widgets = [new DashboardWidget { Type = WidgetType.Unknown, GridX = 0, GridY = 0, GridW = 4, GridH = 2, IsActive = true }];

        Result<bool> result = await service.SaveDashboardAsync(seeded);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Error!.Contains("Unsupported", StringComparison.OrdinalIgnoreCase));
    }
}
