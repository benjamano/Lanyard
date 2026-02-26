using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
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

    [TestMethod]
    public async Task DashboardService_SaveDashboard_CreatesDashboardWithWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);

        Dashboard dashboard = new()
        {
            Id = Guid.Empty,
            Name = "Ops Dashboard",
            IsActive = true,
            CreateDate = DateTime.UtcNow,
            Widgets =
            [
                new DashboardWidget
                {
                    Id = Guid.Empty,
                    Type = "Clock",
                    GridX = 0,
                    GridY = 0,
                    GridW = 4,
                    GridH = 3,
                    SortOrder = 0,
                    IsActive = true
                }
            ]
        };

        Result<Dashboard> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual(1, result.Data.Widgets.Count);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesAndSoftDeletesRemovedWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);

        Result<Dashboard> createResult = await service.SaveDashboardAsync(new Dashboard
        {
            Id = Guid.Empty,
            Name = "Ops Dashboard",
            IsActive = true,
            CreateDate = DateTime.UtcNow,
            Widgets =
            [
                new DashboardWidget
                {
                    Id = Guid.NewGuid(),
                    Type = "Clock",
                    GridX = 0,
                    GridY = 0,
                    GridW = 4,
                    GridH = 3,
                    SortOrder = 0,
                    IsActive = true
                },
                new DashboardWidget
                {
                    Id = Guid.NewGuid(),
                    Type = "MusicControls",
                    GridX = 4,
                    GridY = 0,
                    GridW = 4,
                    GridH = 3,
                    SortOrder = 1,
                    IsActive = true
                }
            ]
        });

        Assert.IsTrue(createResult.Success, createResult.Error);
        Dashboard existing = createResult.Data!;
        DashboardWidget keepWidget = existing.Widgets.First();

        keepWidget.GridW = 6;
        existing.Widgets = [keepWidget];

        Result<Dashboard> updateResult = await service.SaveDashboardAsync(existing);

        Assert.IsTrue(updateResult.Success, updateResult.Error);
        Assert.AreEqual(1, updateResult.Data!.Widgets.Count);

        await using ApplicationDbContext ctx = new(options);
        Dashboard dbDashboard = await ctx.Dashboards.Include(x => x.Widgets).FirstAsync(x => x.Id == existing.Id);
        Assert.AreEqual(2, dbDashboard.Widgets.Count);
        Assert.AreEqual(1, dbDashboard.Widgets.Count(x => x.IsActive));
        Assert.AreEqual(1, dbDashboard.Widgets.Count(x => x.IsActive == false));
    }

    [TestMethod]
    public async Task DashboardService_DeleteDashboard_SetsInactiveOnDashboardAndWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);

        Result<Dashboard> createResult = await service.SaveDashboardAsync(new Dashboard
        {
            Id = Guid.Empty,
            Name = "Delete Me",
            IsActive = true,
            CreateDate = DateTime.UtcNow,
            Widgets =
            [
                new DashboardWidget
                {
                    Id = Guid.Empty,
                    Type = "Clock",
                    GridX = 0,
                    GridY = 0,
                    GridW = 4,
                    GridH = 3,
                    SortOrder = 0,
                    IsActive = true
                }
            ]
        });

        Assert.IsTrue(createResult.Success, createResult.Error);

        Result<bool> deleteResult = await service.DeleteDashboardAsync(createResult.Data!.Id);

        Assert.IsTrue(deleteResult.Success, deleteResult.Error);

        await using ApplicationDbContext ctx = new(options);
        Dashboard dashboard = await ctx.Dashboards.Include(x => x.Widgets).FirstAsync(x => x.Id == createResult.Data.Id);
        Assert.IsFalse(dashboard.IsActive);
        Assert.IsTrue(dashboard.Widgets.All(x => x.IsActive == false));
    }

    [TestMethod]
    public async Task DashboardService_GetDashboardForRender_ReturnsOnlyActiveWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);

        Result<Dashboard> createResult = await service.SaveDashboardAsync(new Dashboard
        {
            Id = Guid.Empty,
            Name = "Render Test",
            IsActive = true,
            CreateDate = DateTime.UtcNow,
            Widgets =
            [
                new DashboardWidget
                {
                    Id = Guid.Empty,
                    Type = "Clock",
                    GridX = 0,
                    GridY = 0,
                    GridW = 4,
                    GridH = 3,
                    SortOrder = 0,
                    IsActive = true
                }
            ]
        });

        Assert.IsTrue(createResult.Success, createResult.Error);

        Dashboard updateDashboard = createResult.Data!;
        updateDashboard.Widgets = [];
        await service.SaveDashboardAsync(updateDashboard);

        Result<Dashboard> renderResult = await service.GetDashboardForRenderAsync(createResult.Data!.Id);
        Assert.IsTrue(renderResult.Success, renderResult.Error);
        Assert.AreEqual(0, renderResult.Data!.Widgets.Count);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_FailsOnInvalidGridBounds()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);

        Result<Dashboard> result = await service.SaveDashboardAsync(new Dashboard
        {
            Id = Guid.Empty,
            Name = "Invalid Grid",
            IsActive = true,
            CreateDate = DateTime.UtcNow,
            Widgets =
            [
                new DashboardWidget
                {
                    Id = Guid.Empty,
                    Type = "Clock",
                    GridX = 10,
                    GridY = 0,
                    GridW = 4,
                    GridH = 2,
                    SortOrder = 0,
                    IsActive = true
                }
            ]
        });

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Error!.Contains("bounds", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_FailsOnUnknownWidgetType()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);

        Result<Dashboard> result = await service.SaveDashboardAsync(new Dashboard
        {
            Id = Guid.Empty,
            Name = "Unknown Widget",
            IsActive = true,
            CreateDate = DateTime.UtcNow,
            Widgets =
            [
                new DashboardWidget
                {
                    Id = Guid.Empty,
                    Type = "Unknown",
                    GridX = 0,
                    GridY = 0,
                    GridW = 4,
                    GridH = 2,
                    SortOrder = 0,
                    IsActive = true
                }
            ]
        });

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Error!.Contains("Unsupported", StringComparison.OrdinalIgnoreCase));
    }
}
