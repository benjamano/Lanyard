using Lanyard.Application.Services;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
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

    private static async Task<Dashboard> SeedDashboardAsync(DbContextOptions<ApplicationDbContext> options, string name = "Test Dashboard")
    {
        await using ApplicationDbContext ctx = new(options);

        Dashboard dashboard = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsActive = true,
            CreateDate = DateTime.UtcNow
        };

        ctx.Dashboards.Add(dashboard);
        await ctx.SaveChangesAsync();

        return dashboard;
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_CreatesNewWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        dashboard.Widgets =
        [
            new DigitalClockWidget
            {
                Id = Guid.Empty,
                GridX = 0,
                GridY = 0,
                GridW = 4,
                GridH = 3,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Dashboard dbDashboard = await ctx.Dashboards.Include(x => x.Widgets).FirstAsync(x => x.Id == dashboard.Id);
        Assert.HasCount(1, dbDashboard.Widgets);
        Assert.IsInstanceOfType<DigitalClockWidget>(dbDashboard.Widgets.Single());
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesAndSoftDeletesRemovedWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        DigitalClockWidget keepWidget = new()
        {
            Id = Guid.NewGuid(),
            GridX = 0,
            GridY = 0,
            GridW = 4,
            GridH = 3,
            IsActive = true
        };

        TextAreaWidget removeWidget = new()
        {
            Id = Guid.NewGuid(),
            GridX = 4,
            GridY = 0,
            GridW = 4,
            GridH = 3,
            IsActive = true
        };

        dashboard.Widgets = [keepWidget, removeWidget];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        keepWidget.GridW = 6;
        dashboard.Widgets = [keepWidget];

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        Dashboard dbDashboard = await ctx.Dashboards.Include(x => x.Widgets).FirstAsync(x => x.Id == dashboard.Id);
        Assert.HasCount(2, dbDashboard.Widgets);
        Assert.AreEqual(1, dbDashboard.Widgets.Count(x => x.IsActive));
        Assert.AreEqual(1, dbDashboard.Widgets.Count(x => x.IsActive == false));
        Assert.AreEqual(6, dbDashboard.Widgets.First(x => x.Id == keepWidget.Id).GridW);
    }

    [TestMethod]
    public async Task DashboardService_DeleteDashboard_SetsInactiveOnDashboardAndWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options, "Delete Me");

        dashboard.Widgets =
        [
            new DigitalClockWidget
            {
                Id = Guid.Empty,
                GridX = 0,
                GridY = 0,
                GridW = 4,
                GridH = 3,
                IsActive = true
            }
        ];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        Result<bool> deleteResult = await service.DeleteDashboardAsync(dashboard.Id);
        Assert.IsTrue(deleteResult.Success, deleteResult.Error);

        await using ApplicationDbContext ctx = new(options);
        Dashboard dbDashboard = await ctx.Dashboards.Include(x => x.Widgets).FirstAsync(x => x.Id == dashboard.Id);
        Assert.IsFalse(dbDashboard.IsActive);
        Assert.IsTrue(dbDashboard.Widgets.All(x => x.IsActive == false));
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_FailsOnUnsupportedWidgetType()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options, "Unknown Widget");

        dashboard.Widgets =
        [
            new DashboardWidget
            {
                Id = Guid.Empty,
                Type = WidgetType.Unknown,
                GridX = 0,
                GridY = 0,
                GridW = 4,
                GridH = 2,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Error!.Contains("Unsupported", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsNewButtonWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        Guid clientId = Guid.NewGuid();
        Guid projectionProgramId = Guid.NewGuid();

        dashboard.Widgets =
        [
            new ButtonWidget
            {
                Id = Guid.Empty,
                Label = "Show Intro",
                Appearance = ButtonAppearance.Outline,
                ActionType = ButtonActionType.TriggerProjectionProgram,
                ClientId = clientId,
                ProjectionProgramId = projectionProgramId,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        ButtonWidget dbWidget = await ctx.DashboardWidgets.OfType<ButtonWidget>().SingleAsync(x => x.DashboardId == dashboard.Id);
        Assert.AreEqual("Show Intro", dbWidget.Label);
        Assert.AreEqual(ButtonAppearance.Outline, dbWidget.Appearance);
        Assert.AreEqual(ButtonActionType.TriggerProjectionProgram, dbWidget.ActionType);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.AreEqual(projectionProgramId, dbWidget.ProjectionProgramId);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesExistingButtonWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        ButtonWidget buttonWidget = new()
        {
            Id = Guid.NewGuid(),
            Label = "Original",
            Appearance = ButtonAppearance.Primary,
            ActionType = null,
            ClientId = null,
            ProjectionProgramId = null,
            IsActive = true
        };

        dashboard.Widgets = [buttonWidget];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        Guid clientId = Guid.NewGuid();
        Guid projectionProgramId = Guid.NewGuid();

        buttonWidget.Label = "Updated";
        buttonWidget.Appearance = ButtonAppearance.Subtle;
        buttonWidget.ActionType = ButtonActionType.TriggerProjectionProgram;
        buttonWidget.ClientId = clientId;
        buttonWidget.ProjectionProgramId = projectionProgramId;

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        ButtonWidget dbWidget = await ctx.DashboardWidgets.OfType<ButtonWidget>().SingleAsync(x => x.Id == buttonWidget.Id);
        Assert.AreEqual("Updated", dbWidget.Label);
        Assert.AreEqual(ButtonAppearance.Subtle, dbWidget.Appearance);
        Assert.AreEqual(ButtonActionType.TriggerProjectionProgram, dbWidget.ActionType);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.AreEqual(projectionProgramId, dbWidget.ProjectionProgramId);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_CopiesButtonActionConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        ButtonWidget existingWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            Label = "Original",
            Appearance = ButtonAppearance.Primary,
            IsActive = true
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(existingWidget);
            await seedCtx.SaveChangesAsync();
        }

        Guid clientId = Guid.NewGuid();
        Guid projectionProgramId = Guid.NewGuid();

        ButtonWidget incomingWidget = new()
        {
            Id = existingWidget.Id,
            DashboardId = dashboard.Id,
            Label = "Updated",
            Appearance = ButtonAppearance.Transparent,
            ActionType = ButtonActionType.TriggerProjectionProgram,
            ClientId = clientId,
            ProjectionProgramId = projectionProgramId,
            IsActive = true
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        ButtonWidget dbWidget = await ctx.DashboardWidgets.OfType<ButtonWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.AreEqual("Updated", dbWidget.Label);
        Assert.AreEqual(ButtonAppearance.Transparent, dbWidget.Appearance);
        Assert.AreEqual(ButtonActionType.TriggerProjectionProgram, dbWidget.ActionType);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.AreEqual(projectionProgramId, dbWidget.ProjectionProgramId);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsNewMusicPlaylistSelectorWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        Guid clientId = Guid.NewGuid();

        dashboard.Widgets =
        [
            new MusicPlaylistSelectorWidget
            {
                Id = Guid.Empty,
                ClientId = clientId,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        MusicPlaylistSelectorWidget dbWidget = await ctx.DashboardWidgets.OfType<MusicPlaylistSelectorWidget>().SingleAsync(x => x.DashboardId == dashboard.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesExistingMusicPlaylistSelectorWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        MusicPlaylistSelectorWidget playlistSelectorWidget = new()
        {
            Id = Guid.NewGuid(),
            ClientId = null,
            IsActive = true
        };

        dashboard.Widgets = [playlistSelectorWidget];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        Guid clientId = Guid.NewGuid();

        playlistSelectorWidget.ClientId = clientId;

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        MusicPlaylistSelectorWidget dbWidget = await ctx.DashboardWidgets.OfType<MusicPlaylistSelectorWidget>().SingleAsync(x => x.Id == playlistSelectorWidget.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_CopiesMusicPlaylistSelectorConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        MusicPlaylistSelectorWidget existingWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            ClientId = null,
            IsActive = true
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(existingWidget);
            await seedCtx.SaveChangesAsync();
        }

        Guid clientId = Guid.NewGuid();

        MusicPlaylistSelectorWidget incomingWidget = new()
        {
            Id = existingWidget.Id,
            DashboardId = dashboard.Id,
            ClientId = clientId,
            IsActive = true
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        MusicPlaylistSelectorWidget dbWidget = await ctx.DashboardWidgets.OfType<MusicPlaylistSelectorWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsNewMusicTimelineWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        Guid clientId = Guid.NewGuid();

        dashboard.Widgets =
        [
            new MusicTimelineWidget
            {
                Id = Guid.Empty,
                ClientId = clientId,
                ShowSongTitle = false,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        MusicTimelineWidget dbWidget = await ctx.DashboardWidgets.OfType<MusicTimelineWidget>().SingleAsync(x => x.DashboardId == dashboard.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.IsFalse(dbWidget.ShowSongTitle);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesExistingMusicTimelineWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        MusicTimelineWidget timelineWidget = new()
        {
            Id = Guid.NewGuid(),
            ClientId = null,
            ShowSongTitle = false,
            IsActive = true
        };

        dashboard.Widgets = [timelineWidget];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        Guid clientId = Guid.NewGuid();

        timelineWidget.ClientId = clientId;
        timelineWidget.ShowSongTitle = true;

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        MusicTimelineWidget dbWidget = await ctx.DashboardWidgets.OfType<MusicTimelineWidget>().SingleAsync(x => x.Id == timelineWidget.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.IsTrue(dbWidget.ShowSongTitle);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_CopiesMusicTimelineConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        MusicTimelineWidget existingWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            ClientId = null,
            ShowSongTitle = true,
            IsActive = true
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(existingWidget);
            await seedCtx.SaveChangesAsync();
        }

        Guid clientId = Guid.NewGuid();

        MusicTimelineWidget incomingWidget = new()
        {
            Id = existingWidget.Id,
            DashboardId = dashboard.Id,
            ClientId = clientId,
            ShowSongTitle = false,
            IsActive = true
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        MusicTimelineWidget dbWidget = await ctx.DashboardWidgets.OfType<MusicTimelineWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.IsFalse(dbWidget.ShowSongTitle);
    }
}
