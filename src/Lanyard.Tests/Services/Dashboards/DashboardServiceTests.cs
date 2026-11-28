using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
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

    private static DashboardService GetService(DbContextOptions<ApplicationDbContext> options, params string[] roles)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        // Most tests act as an Admin; pass roles to act as someone else.
        string[] actingRoles = roles.Length == 0 ? ["Admin"] : roles;
        Mock<ISecurityService> security = new();
        security.Setup(x => x.IsCurrentUserInRoleAsync(It.IsAny<string>()))
            .ReturnsAsync((string role) => actingRoles.Contains(role));

        return new DashboardService(factoryMock.Object, security.Object);
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

    private static async Task<string> SeedUserAsync(DbContextOptions<ApplicationDbContext> options, string userId = "test-user")
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.Users.Add(new UserProfile { Id = userId, UserName = userId, FirstName = "Test", LastName = "User" });
        await ctx.SaveChangesAsync();

        return userId;
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_CreatesNewWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        // Deliberately mirrors what the dashboard editor sends - a bare new widget, with IsActive
        // left alone. Saving it inactive is what made a home screen dashboard render empty.
        dashboard.Widgets =
        [
            new DigitalClockWidget
            {
                Id = Guid.Empty,
                GridX = 0,
                GridY = 0,
                GridW = 4,
                GridH = 3
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Dashboard dbDashboard = await ctx.Dashboards.Include(x => x.Widgets).FirstAsync(x => x.Id == dashboard.Id);
        Assert.HasCount(1, dbDashboard.Widgets);
        Assert.IsInstanceOfType<DigitalClockWidget>(dbDashboard.Widgets.Single());
        Assert.IsTrue(dbDashboard.Widgets.Single().IsActive);
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
            GridH = 3
        };

        TextAreaWidget removeWidget = new()
        {
            Id = Guid.NewGuid(),
            GridX = 4,
            GridY = 0,
            GridW = 4,
            GridH = 3
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
    public async Task DashboardService_SaveDashboard_ReactivatesWidgetStoredAsInactive()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        // Rows written before the save path set IsActive - re-saving the dashboard should heal them.
        DigitalClockWidget storedWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            IsActive = false
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(storedWidget);
            await seedCtx.SaveChangesAsync();
        }

        dashboard.Widgets = [new DigitalClockWidget { Id = storedWidget.Id, DashboardId = dashboard.Id }];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        DashboardWidget dbWidget = await ctx.DashboardWidgets.SingleAsync(x => x.Id == storedWidget.Id);
        Assert.IsTrue(dbWidget.IsActive);
    }

    [TestMethod]
    public async Task DashboardService_GetDashboard_ExcludesSoftDeletedWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        DigitalClockWidget activeWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            IsActive = true
        };

        TextAreaWidget deletedWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            IsActive = false
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.AddRange(activeWidget, deletedWidget);
            await seedCtx.SaveChangesAsync();
        }

        Result<Dashboard> result = await service.GetDashboardAsync(dashboard.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Data);
        Assert.HasCount(1, result.Data.Widgets);
        Assert.AreEqual(activeWidget.Id, result.Data.Widgets.Single().Id);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_DoesNotDeactivateWidget()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        DigitalClockWidget existingWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            IsActive = true
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(existingWidget);
            await seedCtx.SaveChangesAsync();
        }

        DigitalClockWidget incomingWidget = new()
        {
            Id = existingWidget.Id,
            DashboardId = dashboard.Id,
            Is24HourFormat = true,
            IsActive = false
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        DigitalClockWidget dbWidget = await ctx.DashboardWidgets.OfType<DigitalClockWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.IsTrue(dbWidget.Is24HourFormat);
        Assert.IsTrue(dbWidget.IsActive);
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
                SkipToPreviousStep = true,
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
        Assert.IsTrue(dbWidget.SkipToPreviousStep);
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
            SkipToPreviousStep = false,
            IsActive = true
        };

        dashboard.Widgets = [buttonWidget];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        Guid clientId = Guid.NewGuid();
        Guid projectionProgramId = Guid.NewGuid();

        buttonWidget.Label = "Updated";
        buttonWidget.Appearance = ButtonAppearance.Subtle;
        buttonWidget.ActionType = ButtonActionType.SkipProjectionProgramStep;
        buttonWidget.ClientId = clientId;
        buttonWidget.ProjectionProgramId = projectionProgramId;
        buttonWidget.SkipToPreviousStep = true;

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        ButtonWidget dbWidget = await ctx.DashboardWidgets.OfType<ButtonWidget>().SingleAsync(x => x.Id == buttonWidget.Id);
        Assert.AreEqual("Updated", dbWidget.Label);
        Assert.AreEqual(ButtonAppearance.Subtle, dbWidget.Appearance);
        Assert.AreEqual(ButtonActionType.SkipProjectionProgramStep, dbWidget.ActionType);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.AreEqual(projectionProgramId, dbWidget.ProjectionProgramId);
        Assert.IsTrue(dbWidget.SkipToPreviousStep);
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
            ActionType = ButtonActionType.SkipProjectionProgramStep,
            ClientId = clientId,
            ProjectionProgramId = projectionProgramId,
            SkipToPreviousStep = true,
            IsActive = true
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        ButtonWidget dbWidget = await ctx.DashboardWidgets.OfType<ButtonWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.AreEqual("Updated", dbWidget.Label);
        Assert.AreEqual(ButtonAppearance.Transparent, dbWidget.Appearance);
        Assert.AreEqual(ButtonActionType.SkipProjectionProgramStep, dbWidget.ActionType);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.AreEqual(projectionProgramId, dbWidget.ProjectionProgramId);
        Assert.IsTrue(dbWidget.SkipToPreviousStep);
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
                ShowVolumeAndEq = true,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        MusicTimelineWidget dbWidget = await ctx.DashboardWidgets.OfType<MusicTimelineWidget>().SingleAsync(x => x.DashboardId == dashboard.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.IsFalse(dbWidget.ShowSongTitle);
        Assert.IsTrue(dbWidget.ShowVolumeAndEq);
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
        timelineWidget.ShowVolumeAndEq = true;

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        MusicTimelineWidget dbWidget = await ctx.DashboardWidgets.OfType<MusicTimelineWidget>().SingleAsync(x => x.Id == timelineWidget.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.IsTrue(dbWidget.ShowSongTitle);
        Assert.IsTrue(dbWidget.ShowVolumeAndEq);
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
            ShowVolumeAndEq = true,
            IsActive = true
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        MusicTimelineWidget dbWidget = await ctx.DashboardWidgets.OfType<MusicTimelineWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.IsFalse(dbWidget.ShowSongTitle);
        Assert.IsTrue(dbWidget.ShowVolumeAndEq);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsAndUpdatesLiveCaptureWidget()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        Guid clientId = Guid.NewGuid();

        LiveCaptureWidget widget = new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            DeviceName = "HD Pro Webcam C920",
            EnableAudio = true,
            FillTile = false,
            IsActive = true
        };

        dashboard.Widgets = [widget];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        await using (ApplicationDbContext ctx = new(options))
        {
            LiveCaptureWidget created = await ctx.DashboardWidgets.OfType<LiveCaptureWidget>().SingleAsync(x => x.Id == widget.Id);
            Assert.AreEqual(clientId, created.ClientId);
            Assert.AreEqual("HD Pro Webcam C920", created.DeviceName);
            Assert.IsTrue(created.EnableAudio);
            Assert.IsFalse(created.FillTile);
        }

        Guid otherClientId = Guid.NewGuid();

        widget.ClientId = otherClientId;
        widget.DeviceName = "USB Capture HDMI";
        widget.EnableAudio = false;
        widget.FillTile = true;

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext updatedCtx = new(options);
        LiveCaptureWidget updated = await updatedCtx.DashboardWidgets.OfType<LiveCaptureWidget>().SingleAsync(x => x.Id == widget.Id);
        Assert.AreEqual(otherClientId, updated.ClientId);
        Assert.AreEqual("USB Capture HDMI", updated.DeviceName);
        Assert.IsFalse(updated.EnableAudio);
        Assert.IsTrue(updated.FillTile);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_CopiesLiveCaptureConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        LiveCaptureWidget existingWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            IsActive = true
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(existingWidget);
            await seedCtx.SaveChangesAsync();
        }

        Guid clientId = Guid.NewGuid();

        LiveCaptureWidget incomingWidget = new()
        {
            Id = existingWidget.Id,
            DashboardId = dashboard.Id,
            ClientId = clientId,
            DeviceName = "HD Pro Webcam C920",
            EnableAudio = true,
            FillTile = true,
            IsActive = true
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        LiveCaptureWidget dbWidget = await ctx.DashboardWidgets.OfType<LiveCaptureWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.AreEqual("HD Pro Webcam C920", dbWidget.DeviceName);
        Assert.IsTrue(dbWidget.EnableAudio);
        Assert.IsTrue(dbWidget.FillTile);
    }

    // Guards CreateWidgetCopy: a widget type missing from that switch throws
    // "Unsupported widget type." and fails the whole dashboard save, not just its own widget.
    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsNewGreetingWidget()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        dashboard.Widgets =
        [
            new GreetingWidget
            {
                Id = Guid.Empty,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        GreetingWidget dbWidget = await ctx.DashboardWidgets.OfType<GreetingWidget>().SingleAsync(x => x.DashboardId == dashboard.Id);
        Assert.AreEqual(WidgetType.Greeting, dbWidget.Type);
        Assert.IsTrue(dbWidget.IsActive);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsNewKioskHealthWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        dashboard.Widgets =
        [
            new KioskHealthWidget
            {
                Id = Guid.Empty,
                OnlyShowOffline = true,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        KioskHealthWidget dbWidget = await ctx.DashboardWidgets.OfType<KioskHealthWidget>().SingleAsync(x => x.DashboardId == dashboard.Id);
        Assert.IsTrue(dbWidget.OnlyShowOffline);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesExistingKioskHealthWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        KioskHealthWidget kioskHealthWidget = new()
        {
            Id = Guid.NewGuid(),
            OnlyShowOffline = false,
            IsActive = true
        };

        dashboard.Widgets = [kioskHealthWidget];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        kioskHealthWidget.OnlyShowOffline = true;

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        KioskHealthWidget dbWidget = await ctx.DashboardWidgets.OfType<KioskHealthWidget>().SingleAsync(x => x.Id == kioskHealthWidget.Id);
        Assert.IsTrue(dbWidget.OnlyShowOffline);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_CopiesKioskHealthConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        KioskHealthWidget existingWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            OnlyShowOffline = false,
            IsActive = true
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(existingWidget);
            await seedCtx.SaveChangesAsync();
        }

        KioskHealthWidget incomingWidget = new()
        {
            Id = existingWidget.Id,
            DashboardId = dashboard.Id,
            OnlyShowOffline = true,
            IsActive = true
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        KioskHealthWidget dbWidget = await ctx.DashboardWidgets.OfType<KioskHealthWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.IsTrue(dbWidget.OnlyShowOffline);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsNewHallOfFameWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        Guid clientId = Guid.NewGuid();

        dashboard.Widgets =
        [
            new HallOfFameWidget
            {
                Id = Guid.Empty,
                Period = HallOfFamePeriod.ThisWeek,
                ShowTopScore = true,
                ShowBestAccuracy = false,
                ShowBestTeam = false,
                ClientId = clientId,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        HallOfFameWidget dbWidget = await ctx.DashboardWidgets.OfType<HallOfFameWidget>().SingleAsync(x => x.DashboardId == dashboard.Id);
        Assert.AreEqual(HallOfFamePeriod.ThisWeek, dbWidget.Period);
        Assert.IsTrue(dbWidget.ShowTopScore);
        Assert.IsFalse(dbWidget.ShowBestAccuracy);
        Assert.IsFalse(dbWidget.ShowBestTeam);
        Assert.AreEqual(clientId, dbWidget.ClientId);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesExistingHallOfFameWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        HallOfFameWidget hallOfFameWidget = new()
        {
            Id = Guid.NewGuid(),
            Period = HallOfFamePeriod.Today,
            ShowTopScore = true,
            ShowBestAccuracy = true,
            ShowBestTeam = true,
            ClientId = null,
            IsActive = true
        };

        dashboard.Widgets = [hallOfFameWidget];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        Guid clientId = Guid.NewGuid();

        hallOfFameWidget.Period = HallOfFamePeriod.AllTime;
        hallOfFameWidget.ShowBestAccuracy = false;
        hallOfFameWidget.ClientId = clientId;

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        HallOfFameWidget dbWidget = await ctx.DashboardWidgets.OfType<HallOfFameWidget>().SingleAsync(x => x.Id == hallOfFameWidget.Id);
        Assert.AreEqual(HallOfFamePeriod.AllTime, dbWidget.Period);
        Assert.IsFalse(dbWidget.ShowBestAccuracy);
        Assert.AreEqual(clientId, dbWidget.ClientId);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_CopiesHallOfFameConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        HallOfFameWidget existingWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            Period = HallOfFamePeriod.Today,
            ShowBestTeam = true,
            IsActive = true
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(existingWidget);
            await seedCtx.SaveChangesAsync();
        }

        Guid clientId = Guid.NewGuid();

        HallOfFameWidget incomingWidget = new()
        {
            Id = existingWidget.Id,
            DashboardId = dashboard.Id,
            Period = HallOfFamePeriod.ThisMonth,
            ShowBestTeam = false,
            ClientId = clientId,
            IsActive = true
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        HallOfFameWidget dbWidget = await ctx.DashboardWidgets.OfType<HallOfFameWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.AreEqual(HallOfFamePeriod.ThisMonth, dbWidget.Period);
        Assert.IsFalse(dbWidget.ShowBestTeam);
        Assert.AreEqual(clientId, dbWidget.ClientId);
    }

    [TestMethod]
    public async Task DashboardService_SetDefaultDashboardId_PersistsChoice()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        string userId = await SeedUserAsync(options);

        Result<bool> setResult = await service.SetDefaultDashboardIdAsync(userId, dashboard.Id);

        Assert.IsTrue(setResult.Success, setResult.Error);

        Result<Guid?> getResult = await service.GetDefaultDashboardIdAsync(userId);

        Assert.IsTrue(getResult.Success, getResult.Error);
        Assert.AreEqual(dashboard.Id, getResult.Data);
    }

    [TestMethod]
    public async Task DashboardService_GetDefaultDashboardId_ReturnsNullWhenNeverSet()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        string userId = await SeedUserAsync(options);

        Result<Guid?> result = await service.GetDefaultDashboardIdAsync(userId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task DashboardService_SetDefaultDashboardId_ClearsChoiceWhenNull()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        string userId = await SeedUserAsync(options);

        await service.SetDefaultDashboardIdAsync(userId, dashboard.Id);

        Result<bool> clearResult = await service.SetDefaultDashboardIdAsync(userId, null);

        Assert.IsTrue(clearResult.Success, clearResult.Error);

        Result<Guid?> getResult = await service.GetDefaultDashboardIdAsync(userId);

        Assert.IsTrue(getResult.Success, getResult.Error);
        Assert.IsNull(getResult.Data);
    }

    [TestMethod]
    public async Task DashboardService_SetDefaultDashboardId_FailsForUnknownDashboard()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        string userId = await SeedUserAsync(options);

        Result<bool> result = await service.SetDefaultDashboardIdAsync(userId, Guid.NewGuid());

        Assert.IsFalse(result.Success);

        Result<Guid?> getResult = await service.GetDefaultDashboardIdAsync(userId);

        Assert.IsNull(getResult.Data);
    }

    [TestMethod]
    public async Task DashboardService_SetDefaultDashboardId_FailsForDeletedDashboard()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        string userId = await SeedUserAsync(options);

        await service.DeleteDashboardAsync(dashboard.Id);

        Result<bool> result = await service.SetDefaultDashboardIdAsync(userId, dashboard.Id);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task DashboardService_SetDefaultDashboardId_FailsForBlankUserId()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        Result<bool> result = await service.SetDefaultDashboardIdAsync("  ", dashboard.Id);

        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public async Task DashboardService_GetDefaultDashboardId_StillReturnsIdAfterDashboardDeleted()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        string userId = await SeedUserAsync(options);

        await service.SetDefaultDashboardIdAsync(userId, dashboard.Id);
        await service.DeleteDashboardAsync(dashboard.Id);

        // The service deliberately does not clear or validate the stored id - callers need to be
        // able to tell "was set, but the dashboard is gone" apart from "never set" so they can
        // explain the fallback rather than silently showing the standard home page.
        Result<Guid?> getResult = await service.GetDefaultDashboardIdAsync(userId);

        Assert.IsTrue(getResult.Success, getResult.Error);
        Assert.AreEqual(dashboard.Id, getResult.Data);

        // The dashboard itself is gone, though: it no longer loads (so a kiosk or an old designer
        // tab can't keep showing or saving it).
        Result<Dashboard> dashboardResult = await service.GetDashboardAsync(dashboard.Id);

        Assert.IsFalse(dashboardResult.Success);
    }

    [TestMethod]
    public async Task DashboardService_SetOrganisationDefaultDashboardId_PersistsChoice()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        Result<bool> setResult = await service.SetOrganisationDefaultDashboardIdAsync(dashboard.Id);

        Assert.IsTrue(setResult.Success, setResult.Error);

        Result<Guid?> getResult = await service.GetOrganisationDefaultDashboardIdAsync();

        Assert.IsTrue(getResult.Success, getResult.Error);
        Assert.AreEqual(dashboard.Id, getResult.Data);
    }

    [TestMethod]
    public async Task DashboardService_GetOrganisationDefaultDashboardId_ReturnsNullWhenNeverSet()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);

        Result<Guid?> result = await service.GetOrganisationDefaultDashboardIdAsync();

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task DashboardService_SetOrganisationDefaultDashboardId_ClearsChoiceWhenNull()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        await service.SetOrganisationDefaultDashboardIdAsync(dashboard.Id);

        Result<bool> clearResult = await service.SetOrganisationDefaultDashboardIdAsync(null);

        Assert.IsTrue(clearResult.Success, clearResult.Error);

        Result<Guid?> getResult = await service.GetOrganisationDefaultDashboardIdAsync();

        Assert.IsTrue(getResult.Success, getResult.Error);
        Assert.IsNull(getResult.Data);
    }

    [TestMethod]
    public async Task DashboardService_SetOrganisationDefaultDashboardId_ReplacesPreviousChoice()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard first = await SeedDashboardAsync(options, "First");
        Dashboard second = await SeedDashboardAsync(options, "Second");

        await service.SetOrganisationDefaultDashboardIdAsync(first.Id);
        await service.SetOrganisationDefaultDashboardIdAsync(second.Id);

        // A single AppSettings row is what stops two dashboards ever being the default at once.
        Result<Guid?> getResult = await service.GetOrganisationDefaultDashboardIdAsync();

        Assert.IsTrue(getResult.Success, getResult.Error);
        Assert.AreEqual(second.Id, getResult.Data);
    }

    [TestMethod]
    public async Task DashboardService_SetOrganisationDefaultDashboardId_FailsForDeletedDashboard()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        await service.DeleteDashboardAsync(dashboard.Id);

        Result<bool> result = await service.SetOrganisationDefaultDashboardIdAsync(dashboard.Id);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("Dashboard not found.", result.Error);
    }

    [TestMethod]
    public async Task DashboardService_GetHomeScreenDashboard_FallsBackToOrganisationDefault()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        string userId = await SeedUserAsync(options);

        await service.SetOrganisationDefaultDashboardIdAsync(dashboard.Id);

        Result<HomeScreenDashboardSelection> result = await service.GetHomeScreenDashboardAsync(userId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(dashboard.Id, result.Data!.DashboardId);
        Assert.IsTrue(result.Data.IsOrganisationDefault);
    }

    [TestMethod]
    public async Task DashboardService_GetHomeScreenDashboard_PrefersUsersOwnChoice()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard organisationDashboard = await SeedDashboardAsync(options, "Everyone");
        Dashboard personalDashboard = await SeedDashboardAsync(options, "Mine");
        string userId = await SeedUserAsync(options);

        await service.SetOrganisationDefaultDashboardIdAsync(organisationDashboard.Id);
        await service.SetDefaultDashboardIdAsync(userId, personalDashboard.Id);

        Result<HomeScreenDashboardSelection> result = await service.GetHomeScreenDashboardAsync(userId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(personalDashboard.Id, result.Data!.DashboardId);
        Assert.IsFalse(result.Data.IsOrganisationDefault);
    }

    [TestMethod]
    public async Task DashboardService_GetHomeScreenDashboard_StandardHomePageBeatsOrganisationDefault()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        string userId = await SeedUserAsync(options);

        await service.SetOrganisationDefaultDashboardIdAsync(dashboard.Id);

        Result<bool> setResult = await service.SetUseStandardHomePageAsync(userId, true);

        Assert.IsTrue(setResult.Success, setResult.Error);

        // Without this the organisation default would be impossible for an individual to opt out of.
        Result<HomeScreenDashboardSelection> result = await service.GetHomeScreenDashboardAsync(userId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(result.Data!.DashboardId);
        Assert.IsFalse(result.Data.IsOrganisationDefault);
    }

    [TestMethod]
    public async Task DashboardService_GetHomeScreenDashboard_ReturnsNothingWhenNoDefaultsExist()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        string userId = await SeedUserAsync(options);

        Result<HomeScreenDashboardSelection> result = await service.GetHomeScreenDashboardAsync(userId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(result.Data!.DashboardId);
        Assert.IsFalse(result.Data.IsOrganisationDefault);
    }

    [TestMethod]
    public async Task DashboardService_SetUseStandardHomePage_ClearsPersonalDashboard()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        string userId = await SeedUserAsync(options);

        await service.SetDefaultDashboardIdAsync(userId, dashboard.Id);
        await service.SetUseStandardHomePageAsync(userId, true);

        // A stale id left behind here would come back the moment the flag was cleared.
        Result<Guid?> getResult = await service.GetDefaultDashboardIdAsync(userId);

        Assert.IsTrue(getResult.Success, getResult.Error);
        Assert.IsNull(getResult.Data);
    }

    [TestMethod]
    public async Task DashboardService_SetDefaultDashboardId_ClearsStandardHomePageChoice()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        string userId = await SeedUserAsync(options);

        await service.SetUseStandardHomePageAsync(userId, true);
        await service.SetDefaultDashboardIdAsync(userId, dashboard.Id);

        // Otherwise the flag would keep overriding the dashboard the user just picked.
        Result<HomeScreenDashboardSelection> result = await service.GetHomeScreenDashboardAsync(userId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(dashboard.Id, result.Data!.DashboardId);
    }

    [TestMethod]
    public async Task DashboardService_GetHomeScreenDashboard_ReturnsToOrganisationDefaultAfterClearingStandardHomePage()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        string userId = await SeedUserAsync(options);

        await service.SetOrganisationDefaultDashboardIdAsync(dashboard.Id);
        await service.SetUseStandardHomePageAsync(userId, true);
        await service.SetUseStandardHomePageAsync(userId, false);

        Result<HomeScreenDashboardSelection> result = await service.GetHomeScreenDashboardAsync(userId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(dashboard.Id, result.Data!.DashboardId);
        Assert.IsTrue(result.Data.IsOrganisationDefault);
    }

    [TestMethod]
    public async Task DashboardService_GetHomeScreenDashboard_FailsForBlankUserId()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);

        Result<HomeScreenDashboardSelection> result = await service.GetHomeScreenDashboardAsync("  ");

        Assert.IsFalse(result.Success);
        Assert.AreEqual("User id is required.", result.Error);
    }

    [TestMethod]
    public async Task DashboardService_DeleteDashboard_ClearsItAsTheOrganisationDefault()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        await service.SetOrganisationDefaultDashboardIdAsync(dashboard.Id);
        await service.DeleteDashboardAsync(dashboard.Id);

        // A stale organisation default cannot be cleared from the UI - the dashboards list only
        // renders active dashboards, so there is no row left to click - while everyone inheriting
        // it keeps being sent to the standard home page. Deleting has to clear it.
        Result<Guid?> getResult = await service.GetOrganisationDefaultDashboardIdAsync();

        Assert.IsTrue(getResult.Success, getResult.Error);
        Assert.IsNull(getResult.Data);
    }

    [TestMethod]
    public async Task DashboardService_DeleteDashboard_LeavesADifferentOrganisationDefaultAlone()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard organisationDashboard = await SeedDashboardAsync(options, "Everyone");
        Dashboard otherDashboard = await SeedDashboardAsync(options, "Other");

        await service.SetOrganisationDefaultDashboardIdAsync(organisationDashboard.Id);
        await service.DeleteDashboardAsync(otherDashboard.Id);

        Result<Guid?> getResult = await service.GetOrganisationDefaultDashboardIdAsync();

        Assert.IsTrue(getResult.Success, getResult.Error);
        Assert.AreEqual(organisationDashboard.Id, getResult.Data);
    }

    [TestMethod]
    public async Task DashboardService_GetHomeScreenDashboard_FallsThroughToOrganisationDefaultWhenOwnDashboardDeleted()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard organisationDashboard = await SeedDashboardAsync(options, "Everyone");
        Dashboard personalDashboard = await SeedDashboardAsync(options, "Mine");
        string userId = await SeedUserAsync(options);

        await service.SetOrganisationDefaultDashboardIdAsync(organisationDashboard.Id);
        await service.SetDefaultDashboardIdAsync(userId, personalDashboard.Id);
        await service.DeleteDashboardAsync(personalDashboard.Id);

        // A choice pointing at a deleted dashboard is no longer a choice, so it must not shadow
        // the organisation default and drop the user all the way to the standard home page.
        Result<HomeScreenDashboardSelection> result = await service.GetHomeScreenDashboardAsync(userId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(organisationDashboard.Id, result.Data!.DashboardId);
        Assert.IsTrue(result.Data.IsOrganisationDefault);
        Assert.IsTrue(result.Data.PersonalDashboardUnavailable);
    }

    [TestMethod]
    public async Task DashboardService_GetHomeScreenDashboard_ReportsNothingWhenOwnDashboardDeletedAndNoOrganisationDefault()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard personalDashboard = await SeedDashboardAsync(options);
        string userId = await SeedUserAsync(options);

        await service.SetDefaultDashboardIdAsync(userId, personalDashboard.Id);
        await service.DeleteDashboardAsync(personalDashboard.Id);

        Result<HomeScreenDashboardSelection> result = await service.GetHomeScreenDashboardAsync(userId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(result.Data!.DashboardId);
        Assert.IsFalse(result.Data.IsOrganisationDefault);
        Assert.IsTrue(result.Data.PersonalDashboardUnavailable);
    }

    [TestMethod]
    public async Task DashboardService_GetHomeScreenDashboard_IgnoresAnOrganisationDefaultDeletedOutsideTheApp()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        string userId = await SeedUserAsync(options);

        await service.SetOrganisationDefaultDashboardIdAsync(dashboard.Id);

        // Deactivated directly rather than through DeleteDashboardAsync, which would have cleared
        // the setting - this is the state a row edited outside the app leaves behind.
        await using (ApplicationDbContext ctx = new(options))
        {
            Dashboard tracked = await ctx.Dashboards.FirstAsync(x => x.Id == dashboard.Id);
            tracked.IsActive = false;
            await ctx.SaveChangesAsync();
        }

        Result<HomeScreenDashboardSelection> result = await service.GetHomeScreenDashboardAsync(userId);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(result.Data!.DashboardId);
        Assert.IsFalse(result.Data.IsOrganisationDefault);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsNewMyTrainingWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        dashboard.Widgets =
        [
            new MyTrainingWidget
            {
                Id = Guid.Empty,
                IncludeCompleted = true,
                MaxItems = 9,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        MyTrainingWidget dbWidget = await ctx.DashboardWidgets.OfType<MyTrainingWidget>().SingleAsync(x => x.DashboardId == dashboard.Id);
        Assert.IsTrue(dbWidget.IncludeCompleted);
        Assert.AreEqual(9, dbWidget.MaxItems);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesExistingMyTrainingWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        MyTrainingWidget myTrainingWidget = new()
        {
            Id = Guid.NewGuid(),
            IncludeCompleted = false,
            MaxItems = 5,
            IsActive = true
        };

        dashboard.Widgets = [myTrainingWidget];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        myTrainingWidget.IncludeCompleted = true;
        myTrainingWidget.MaxItems = 12;

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        MyTrainingWidget dbWidget = await ctx.DashboardWidgets.OfType<MyTrainingWidget>().SingleAsync(x => x.Id == myTrainingWidget.Id);
        Assert.IsTrue(dbWidget.IncludeCompleted);
        Assert.AreEqual(12, dbWidget.MaxItems);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsNewProjectionStatusWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        Guid clientId = Guid.NewGuid();

        dashboard.Widgets =
        [
            new ProjectionStatusWidget
            {
                Id = Guid.Empty,
                ClientId = clientId,
                DisplayIndex = 2,
                ShowControls = true,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        ProjectionStatusWidget dbWidget = await ctx.DashboardWidgets.OfType<ProjectionStatusWidget>().SingleAsync(x => x.DashboardId == dashboard.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.AreEqual(2, dbWidget.DisplayIndex);
        Assert.IsTrue(dbWidget.ShowControls);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesExistingProjectionStatusWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        ProjectionStatusWidget projectionStatusWidget = new()
        {
            Id = Guid.NewGuid(),
            ClientId = null,
            DisplayIndex = null,
            ShowControls = false,
            IsActive = true
        };

        dashboard.Widgets = [projectionStatusWidget];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        Guid clientId = Guid.NewGuid();

        projectionStatusWidget.ClientId = clientId;
        projectionStatusWidget.DisplayIndex = 1;
        projectionStatusWidget.ShowControls = true;

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        ProjectionStatusWidget dbWidget = await ctx.DashboardWidgets.OfType<ProjectionStatusWidget>().SingleAsync(x => x.Id == projectionStatusWidget.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.AreEqual(1, dbWidget.DisplayIndex);
        Assert.IsTrue(dbWidget.ShowControls);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_CopiesProjectionStatusConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        ProjectionStatusWidget existingWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            ClientId = null,
            DisplayIndex = null,
            ShowControls = false,
            IsActive = true
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(existingWidget);
            await seedCtx.SaveChangesAsync();
        }

        Guid clientId = Guid.NewGuid();

        ProjectionStatusWidget incomingWidget = new()
        {
            Id = existingWidget.Id,
            DashboardId = dashboard.Id,
            ClientId = clientId,
            DisplayIndex = 3,
            ShowControls = true,
            IsActive = true
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        ProjectionStatusWidget dbWidget = await ctx.DashboardWidgets.OfType<ProjectionStatusWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.AreEqual(clientId, dbWidget.ClientId);
        Assert.AreEqual(3, dbWidget.DisplayIndex);
        Assert.IsTrue(dbWidget.ShowControls);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_CopiesMyTrainingConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        MyTrainingWidget existingWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            IncludeCompleted = false,
            MaxItems = 5,
            IsActive = true
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(existingWidget);
            await seedCtx.SaveChangesAsync();
        }

        MyTrainingWidget incomingWidget = new()
        {
            Id = existingWidget.Id,
            DashboardId = dashboard.Id,
            IncludeCompleted = true,
            MaxItems = 3,
            IsActive = true
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        MyTrainingWidget dbWidget = await ctx.DashboardWidgets.OfType<MyTrainingWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.IsTrue(dbWidget.IncludeCompleted);
        Assert.AreEqual(3, dbWidget.MaxItems);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsNewAnnouncementsWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        // Id = Guid.Empty forces the CreateWidgetCopy path, whose default arm throws
        // "Unsupported widget type." and fails the whole dashboard save, not just this widget.
        dashboard.Widgets =
        [
            new AnnouncementsWidget
            {
                Id = Guid.Empty,
                MaxItems = 7,
                IsActive = true
            }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        AnnouncementsWidget dbWidget = await ctx.DashboardWidgets.OfType<AnnouncementsWidget>().SingleAsync(x => x.DashboardId == dashboard.Id);
        Assert.AreEqual(WidgetType.Announcements, dbWidget.Type);
        Assert.AreEqual(7, dbWidget.MaxItems);
        Assert.IsTrue(dbWidget.IsActive);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesExistingAnnouncementsWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        AnnouncementsWidget announcementsWidget = new()
        {
            Id = Guid.NewGuid(),
            MaxItems = 3,
            IsActive = true
        };

        dashboard.Widgets = [announcementsWidget];

        Result<bool> createResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(createResult.Success, createResult.Error);

        announcementsWidget.MaxItems = 10;

        Result<bool> updateResult = await service.SaveDashboardAsync(dashboard);
        Assert.IsTrue(updateResult.Success, updateResult.Error);

        await using ApplicationDbContext ctx = new(options);
        AnnouncementsWidget dbWidget = await ctx.DashboardWidgets.OfType<AnnouncementsWidget>().SingleAsync(x => x.Id == announcementsWidget.Id);
        Assert.AreEqual(10, dbWidget.MaxItems);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_CopiesAnnouncementsConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        AnnouncementsWidget existingWidget = new()
        {
            Id = Guid.NewGuid(),
            DashboardId = dashboard.Id,
            MaxItems = 3,
            IsActive = true
        };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(existingWidget);
            await seedCtx.SaveChangesAsync();
        }

        AnnouncementsWidget incomingWidget = new()
        {
            Id = existingWidget.Id,
            DashboardId = dashboard.Id,
            MaxItems = 8,
            IsActive = true
        };

        Result<DashboardWidget> result = await service.SaveWidgetAsync(incomingWidget);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        AnnouncementsWidget dbWidget = await ctx.DashboardWidgets.OfType<AnnouncementsWidget>().SingleAsync(x => x.Id == existingWidget.Id);
        Assert.AreEqual(8, dbWidget.MaxItems);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsNewSchedulingWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        // Id = Guid.Empty forces CreateWidgetCopy, whose default arm fails the whole save.
        dashboard.Widgets =
        [
            new MyShiftsWidget { Id = Guid.Empty, MaxItems = 8, DaysAhead = 21, ShowTimeOff = false, IsActive = true },
            new WhoIsOnTodayWidget { Id = Guid.Empty, LocationId = 3, ShowClockStatus = false, IsActive = true },
            new PendingTimeOffWidget { Id = Guid.Empty, MaxItems = 4, IsActive = true }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        MyShiftsWidget myShifts = await ctx.DashboardWidgets.OfType<MyShiftsWidget>().SingleAsync();
        WhoIsOnTodayWidget whoIsOn = await ctx.DashboardWidgets.OfType<WhoIsOnTodayWidget>().SingleAsync();
        PendingTimeOffWidget pending = await ctx.DashboardWidgets.OfType<PendingTimeOffWidget>().SingleAsync();

        Assert.AreEqual(WidgetType.MyShifts, myShifts.Type);
        Assert.AreEqual(8, myShifts.MaxItems);
        Assert.AreEqual(21, myShifts.DaysAhead);
        Assert.IsFalse(myShifts.ShowTimeOff);
        Assert.AreEqual(WidgetType.WhoIsOnToday, whoIsOn.Type);
        Assert.AreEqual(3, whoIsOn.LocationId);
        Assert.IsFalse(whoIsOn.ShowClockStatus);
        Assert.AreEqual(WidgetType.PendingTimeOff, pending.Type);
        Assert.AreEqual(4, pending.MaxItems);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesExistingSchedulingWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        MyShiftsWidget myShifts = new() { Id = Guid.Empty, IsActive = true };
        WhoIsOnTodayWidget whoIsOn = new() { Id = Guid.Empty, IsActive = true };
        dashboard.Widgets = [myShifts, whoIsOn];
        Assert.IsTrue((await service.SaveDashboardAsync(dashboard)).Success);

        await using (ApplicationDbContext read = new(options))
        {
            myShifts.Id = (await read.DashboardWidgets.OfType<MyShiftsWidget>().SingleAsync()).Id;
            whoIsOn.Id = (await read.DashboardWidgets.OfType<WhoIsOnTodayWidget>().SingleAsync()).Id;
        }

        myShifts.DaysAhead = 7;
        whoIsOn.LocationId = 9;
        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(7, (await ctx.DashboardWidgets.OfType<MyShiftsWidget>().SingleAsync()).DaysAhead);
        Assert.AreEqual(9, (await ctx.DashboardWidgets.OfType<WhoIsOnTodayWidget>().SingleAsync()).LocationId);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_CopiesSchedulingWidgetConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        MyShiftsWidget myShifts = new() { Id = Guid.NewGuid(), DashboardId = dashboard.Id, IsActive = true };
        WhoIsOnTodayWidget whoIsOn = new() { Id = Guid.NewGuid(), DashboardId = dashboard.Id, IsActive = true };
        PendingTimeOffWidget pending = new() { Id = Guid.NewGuid(), DashboardId = dashboard.Id, IsActive = true };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.AddRange(myShifts, whoIsOn, pending);
            await seedCtx.SaveChangesAsync();
        }

        Assert.IsTrue((await service.SaveWidgetAsync(new MyShiftsWidget { Id = myShifts.Id, DashboardId = dashboard.Id, MaxItems = 2, DaysAhead = 3, ShowTimeOff = false, IsActive = true })).Success);
        Assert.IsTrue((await service.SaveWidgetAsync(new WhoIsOnTodayWidget { Id = whoIsOn.Id, DashboardId = dashboard.Id, LocationId = 5, ShowClockStatus = false, IsActive = true })).Success);
        Assert.IsTrue((await service.SaveWidgetAsync(new PendingTimeOffWidget { Id = pending.Id, DashboardId = dashboard.Id, MaxItems = 9, IsActive = true })).Success);

        await using ApplicationDbContext ctx = new(options);
        MyShiftsWidget savedShifts = await ctx.DashboardWidgets.OfType<MyShiftsWidget>().SingleAsync();
        Assert.AreEqual(2, savedShifts.MaxItems);
        Assert.AreEqual(3, savedShifts.DaysAhead);
        Assert.IsFalse(savedShifts.ShowTimeOff);
        Assert.AreEqual(5, (await ctx.DashboardWidgets.OfType<WhoIsOnTodayWidget>().SingleAsync()).LocationId);
        Assert.AreEqual(9, (await ctx.DashboardWidgets.OfType<PendingTimeOffWidget>().SingleAsync()).MaxItems);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsNewComplianceSummaryWidget()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        // Id = Guid.Empty forces CreateWidgetCopy, whose default arm fails the whole save.
        dashboard.Widgets =
        [
            new ComplianceSummaryWidget { Id = Guid.Empty, MaxItems = 6, ExpiryWindowDays = 60, LocationId = 2, IsActive = true }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        ComplianceSummaryWidget saved = await ctx.DashboardWidgets.OfType<ComplianceSummaryWidget>().SingleAsync();

        Assert.AreEqual(WidgetType.ComplianceSummary, saved.Type);
        Assert.AreEqual(6, saved.MaxItems);
        Assert.AreEqual(60, saved.ExpiryWindowDays);
        Assert.AreEqual(2, saved.LocationId);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsAndUpdatesDemandStaffingWidget()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        // Id = Guid.Empty forces CreateWidgetCopy, whose default arm fails the whole save.
        DemandStaffingWidget demand = new() { Id = Guid.Empty, RangeDays = 56, LocationId = 3, IsActive = true };
        dashboard.Widgets = [demand];
        Assert.IsTrue((await service.SaveDashboardAsync(dashboard)).Success);

        await using (ApplicationDbContext read = new(options))
        {
            DemandStaffingWidget created = await read.DashboardWidgets.OfType<DemandStaffingWidget>().SingleAsync();
            Assert.AreEqual(WidgetType.DemandStaffing, created.Type);
            Assert.AreEqual(56, created.RangeDays);
            Assert.AreEqual(3, created.LocationId);
            demand.Id = created.Id;
        }

        demand.RangeDays = 14;
        demand.LocationId = null;
        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        DemandStaffingWidget saved = await ctx.DashboardWidgets.OfType<DemandStaffingWidget>().SingleAsync();
        Assert.AreEqual(14, saved.RangeDays);
        Assert.IsNull(saved.LocationId);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsAndUpdatesWhatsOnTodayWidget()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        // Id = Guid.Empty forces CreateWidgetCopy, whose default arm fails the whole save.
        WhatsOnTodayWidget whatsOn = new() { Id = Guid.Empty, MaxItems = 4, LocationId = 2, IsActive = true };
        dashboard.Widgets = [whatsOn];
        Assert.IsTrue((await service.SaveDashboardAsync(dashboard)).Success);

        await using (ApplicationDbContext read = new(options))
        {
            WhatsOnTodayWidget created = await read.DashboardWidgets.OfType<WhatsOnTodayWidget>().SingleAsync();
            Assert.AreEqual(WidgetType.WhatsOnToday, created.Type);
            Assert.AreEqual(4, created.MaxItems);
            Assert.AreEqual(2, created.LocationId);
            whatsOn.Id = created.Id;
        }

        whatsOn.MaxItems = 10;
        whatsOn.LocationId = null;
        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        WhatsOnTodayWidget saved = await ctx.DashboardWidgets.OfType<WhatsOnTodayWidget>().SingleAsync();
        Assert.AreEqual(10, saved.MaxItems);
        Assert.IsNull(saved.LocationId);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_UpdatesWhatsOnTodaySettings()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        WhatsOnTodayWidget whatsOn = new() { Id = Guid.NewGuid(), DashboardId = dashboard.Id, IsActive = true };

        await using (ApplicationDbContext seed = new(options))
        {
            seed.DashboardWidgets.Add(whatsOn);
            await seed.SaveChangesAsync();
        }

        Assert.IsTrue((await service.SaveWidgetAsync(new WhatsOnTodayWidget { Id = whatsOn.Id, DashboardId = dashboard.Id, MaxItems = 3, LocationId = 7, IsActive = true })).Success);

        await using ApplicationDbContext ctx = new(options);
        WhatsOnTodayWidget saved = await ctx.DashboardWidgets.OfType<WhatsOnTodayWidget>().SingleAsync();
        Assert.AreEqual(3, saved.MaxItems);
        Assert.AreEqual(7, saved.LocationId);
    }

    [TestMethod]
    public void WhatsOnTodayWidget_DefaultsToSixEntriesAtTheSignedInLocation()
    {
        WhatsOnTodayWidget widget = new();

        Assert.AreEqual(WidgetType.WhatsOnToday, widget.Type);
        Assert.AreEqual(6, widget.MaxItems);
        Assert.IsNull(widget.LocationId);
    }

    [TestMethod]
    public void DemandStaffingWidget_DefaultsToFourWeeksAtTheSignedInLocation()
    {
        DemandStaffingWidget widget = new();

        Assert.AreEqual(WidgetType.DemandStaffing, widget.Type);
        Assert.AreEqual(28, widget.RangeDays);
        Assert.IsNull(widget.LocationId);
    }

    [TestMethod]
    public void ComplianceSummaryWidget_DefaultsToAThirtyDayWindowForEveryLocation()
    {
        ComplianceSummaryWidget widget = new();

        Assert.AreEqual(WidgetType.ComplianceSummary, widget.Type);
        Assert.AreEqual(30, widget.ExpiryWindowDays);
        Assert.AreEqual(3, widget.MaxItems);
        Assert.IsNull(widget.LocationId);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_UpdatesExistingComplianceSummaryWidget()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        ComplianceSummaryWidget compliance = new() { Id = Guid.Empty, IsActive = true };
        dashboard.Widgets = [compliance];
        Assert.IsTrue((await service.SaveDashboardAsync(dashboard)).Success);

        await using (ApplicationDbContext read = new(options))
        {
            compliance.Id = (await read.DashboardWidgets.OfType<ComplianceSummaryWidget>().SingleAsync()).Id;
        }

        compliance.ExpiryWindowDays = 90;
        compliance.MaxItems = 0;
        compliance.LocationId = 7;
        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        ComplianceSummaryWidget saved = await ctx.DashboardWidgets.OfType<ComplianceSummaryWidget>().SingleAsync();
        Assert.AreEqual(90, saved.ExpiryWindowDays);
        Assert.AreEqual(0, saved.MaxItems);
        Assert.AreEqual(7, saved.LocationId);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_CopiesComplianceSummaryConfiguration()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        ComplianceSummaryWidget compliance = new() { Id = Guid.NewGuid(), DashboardId = dashboard.Id, IsActive = true };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(compliance);
            await seedCtx.SaveChangesAsync();
        }

        Result<DashboardWidget> result = await service.SaveWidgetAsync(new ComplianceSummaryWidget
        {
            Id = compliance.Id,
            DashboardId = dashboard.Id,
            MaxItems = 10,
            ExpiryWindowDays = 14,
            LocationId = null,
            IsActive = true
        });

        Assert.IsTrue(result.Success, result.Error);

        await using ApplicationDbContext ctx = new(options);
        ComplianceSummaryWidget saved = await ctx.DashboardWidgets.OfType<ComplianceSummaryWidget>().SingleAsync();
        Assert.AreEqual(10, saved.MaxItems);
        Assert.AreEqual(14, saved.ExpiryWindowDays);
        Assert.IsNull(saved.LocationId);
    }

    [TestMethod]
    [DataRow("Admin")]
    [DataRow("Manager")]
    public async Task DashboardService_SaveDashboard_AllowsAdminsAndManagers(string role)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options, role);
        Dashboard dashboard = await SeedDashboardAsync(options);
        dashboard.Name = "Renamed";

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task DashboardService_WritesAreRefusedForStaff()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options, "Staff");
        Dashboard dashboard = await SeedDashboardAsync(options);

        DigitalClockWidget widget = new() { DashboardId = dashboard.Id };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(widget);
            await seedCtx.SaveChangesAsync();
        }

        dashboard.Name = "Hijacked";

        Result<bool> save = await service.SaveDashboardAsync(dashboard);
        Result<Guid> create = await service.CreateDashboardAsync(new Dashboard { Name = "New" });
        Result<bool> delete = await service.DeleteDashboardAsync(dashboard.Id);
        Result<DashboardWidget> saveWidget = await service.SaveWidgetAsync(new DigitalClockWidget { Id = widget.Id, DashboardId = dashboard.Id, Is24HourFormat = true });

        Assert.IsFalse(save.IsSuccess);
        Assert.IsFalse(create.IsSuccess);
        Assert.IsFalse(delete.IsSuccess);
        Assert.IsFalse(saveWidget.IsSuccess);
        Assert.AreEqual(DashboardService.NotAllowedToEditMessage, save.Error);

        await using ApplicationDbContext ctx = new(options);
        Dashboard stored = await ctx.Dashboards.SingleAsync(x => x.Id == dashboard.Id);
        Assert.AreEqual("Test Dashboard", stored.Name);
        Assert.IsTrue(stored.IsActive);
        Assert.AreEqual(1, await ctx.Dashboards.CountAsync());
        Assert.IsFalse((await ctx.DashboardWidgets.OfType<DigitalClockWidget>().SingleAsync()).Is24HourFormat);
    }

    [TestMethod]
    public async Task DashboardService_SaveWidget_DoesNotWriteLayout()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        TextAreaWidget widget = new() { DashboardId = dashboard.Id, GridX = 0, GridY = 0, GridW = 2, GridH = 2 };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(widget);
            await seedCtx.SaveChangesAsync();
        }

        Result<DashboardWidget> result = await service.SaveWidgetAsync(new TextAreaWidget
        {
            Id = widget.Id,
            DashboardId = dashboard.Id,
            Content = "<p>Hello</p>",
            GridX = 6,
            GridY = 3,
            GridW = 4,
            GridH = 1
        });

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        TextAreaWidget stored = await ctx.DashboardWidgets.OfType<TextAreaWidget>().SingleAsync();
        Assert.AreEqual("<p>Hello</p>", stored.Content);
        Assert.AreEqual(0, stored.GridX);
        Assert.AreEqual(0, stored.GridY);
        Assert.AreEqual(2, stored.GridW);
        Assert.AreEqual(2, stored.GridH);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_PersistsLayoutOptions()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        DigitalClockWidget existing = new() { DashboardId = dashboard.Id };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(existing);
            await seedCtx.SaveChangesAsync();
        }

        dashboard.FitToScreen = true;
        dashboard.RowHeight = 999;
        dashboard.Widgets =
        [
            new DigitalClockWidget { Id = existing.Id, Title = " Clock ", ShowTitle = true, HideOnPhone = true, PhoneOrder = 2 },
            new GreetingWidget { Id = Guid.Empty, ShowTitle = true, Title = "Hi", PhoneOrder = 0 }
        ];

        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Dashboard stored = await ctx.Dashboards.SingleAsync();
        Assert.IsTrue(stored.FitToScreen);
        Assert.AreEqual(Dashboard.MaxRowHeight, stored.RowHeight);

        DigitalClockWidget clock = await ctx.DashboardWidgets.OfType<DigitalClockWidget>().SingleAsync();
        Assert.AreEqual("Clock", clock.Title);
        Assert.IsTrue(clock.ShowTitle);
        Assert.IsTrue(clock.HideOnPhone);
        Assert.AreEqual(2, clock.PhoneOrder);

        GreetingWidget greeting = await ctx.DashboardWidgets.OfType<GreetingWidget>().SingleAsync();
        Assert.IsTrue(greeting.ShowTitle);
        Assert.AreEqual(0, greeting.PhoneOrder);
        Assert.IsFalse(greeting.HideOnPhone);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_RefusesToSaveOverSomeoneElsesSave()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard stored = await SeedDashboardAsync(options);

        Dashboard mine = new() { Id = stored.Id, Name = "Mine", IsActive = true, LastUpdateDate = stored.LastUpdateDate };
        Dashboard theirs = new() { Id = stored.Id, Name = "Theirs", IsActive = true, LastUpdateDate = stored.LastUpdateDate };

        Result<bool> theirSave = await service.SaveDashboardAsync(theirs);
        Result<bool> mySave = await service.SaveDashboardAsync(mine);

        Assert.IsTrue(theirSave.IsSuccess, theirSave.Error);
        Assert.IsFalse(mySave.IsSuccess);
        Assert.AreEqual(DashboardService.ChangedElsewhereMessage, mySave.Error);

        Result<bool> overwrite = await service.SaveDashboardAsync(mine, overwrite: true);
        Assert.IsTrue(overwrite.IsSuccess, overwrite.Error);

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual("Mine", (await ctx.Dashboards.SingleAsync()).Name);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_CanSaveTheSameCopyAgain()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        dashboard.Name = "First";
        Result<bool> first = await service.SaveDashboardAsync(dashboard);
        dashboard.Name = "Second";
        Result<bool> second = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(first.IsSuccess, first.Error);
        Assert.IsTrue(second.IsSuccess, second.Error);
        Assert.IsNotNull(dashboard.LastUpdateDate);
    }

    [TestMethod]
    public async Task DashboardService_DuplicateDashboard_CopiesEveryWidgetAndSetting()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options, "Manager");
        Dashboard source = await SeedDashboardAsync(options, "Front desk");

        await using (ApplicationDbContext seedCtx = new(options))
        {
            Dashboard stored = await seedCtx.Dashboards.SingleAsync();
            stored.FitToScreen = true;
            stored.RowHeight = 90;
            seedCtx.DashboardWidgets.Add(new DigitalClockWidget { DashboardId = source.Id, Is24HourFormat = true, GridX = 3, Title = "Time", ShowTitle = true, PhoneOrder = 1 });
            seedCtx.DashboardWidgets.Add(new HallOfFameWidget { DashboardId = source.Id, Period = HallOfFamePeriod.ThisWeek, HideOnPhone = true });
            seedCtx.DashboardWidgets.Add(new GreetingWidget { DashboardId = source.Id, IsActive = false });
            seedCtx.DashboardWidgets.Add(new MusicTimelineWidget { DashboardId = source.Id, ShowSongTitle = false, ShowVolumeAndEq = true });
            await seedCtx.SaveChangesAsync();
        }

        Result<Guid> result = await service.DuplicateDashboardAsync(source.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Dashboard copy = await ctx.Dashboards.Include(x => x.Widgets).SingleAsync(x => x.Id == result.Data);
        Assert.AreEqual("Front desk (copy)", copy.Name);
        Assert.IsTrue(copy.FitToScreen);
        Assert.AreEqual(90, copy.RowHeight);
        Assert.HasCount(3, copy.Widgets);

        DigitalClockWidget clock = copy.Widgets.OfType<DigitalClockWidget>().Single();
        Assert.IsTrue(clock.Is24HourFormat);
        Assert.AreEqual(3, clock.GridX);
        Assert.AreEqual("Time", clock.Title);
        Assert.AreEqual(1, clock.PhoneOrder);
        Assert.AreEqual(HallOfFamePeriod.ThisWeek, copy.Widgets.OfType<HallOfFameWidget>().Single().Period);
        Assert.IsTrue(copy.Widgets.OfType<HallOfFameWidget>().Single().HideOnPhone);
        MusicTimelineWidget timeline = copy.Widgets.OfType<MusicTimelineWidget>().Single();
        Assert.IsFalse(timeline.ShowSongTitle);
        Assert.IsTrue(timeline.ShowVolumeAndEq);

        // The original is untouched.
        Assert.AreEqual(3, await ctx.DashboardWidgets.CountAsync(x => x.DashboardId == source.Id && x.IsActive));
    }

    [TestMethod]
    public async Task DashboardService_DuplicateDashboard_IsRefusedForStaff()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options, "Staff");
        Dashboard source = await SeedDashboardAsync(options);

        Result<Guid> result = await service.DuplicateDashboardAsync(source.Id);

        Assert.IsFalse(result.IsSuccess);
        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.Dashboards.CountAsync());
    }

    [TestMethod]
    public async Task DashboardService_CreateDashboard_StartsWithTheGivenWidgets()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);

        Result<Guid> result = await service.CreateDashboardAsync(new Dashboard
        {
            Name = "From a template",
            RowHeight = 160,
            Widgets = [new GreetingWidget { GridW = 8 }, new MyShiftsWidget { GridY = 1, DaysAhead = 7 }]
        });

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Dashboard created = await ctx.Dashboards.Include(x => x.Widgets).SingleAsync();
        Assert.AreEqual(result.Data, created.Id);
        Assert.AreEqual(160, created.RowHeight);
        Assert.HasCount(2, created.Widgets);
        Assert.AreEqual(7, created.Widgets.OfType<MyShiftsWidget>().Single().DaysAhead);
    }

    [TestMethod]
    public async Task DashboardService_GetDashboardCards_ListsLayoutsAndTheProgramsShowingThem()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options, "Scoreboard TV");

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(new ClientZoneLaserScoreboardWidget { DashboardId = dashboard.Id, GridX = 0, GridY = 0, GridW = 8, GridH = 4 });
            seedCtx.DashboardWidgets.Add(new DigitalClockWidget { DashboardId = dashboard.Id, IsActive = false });

            ProjectionProgramStepTemplateParameter parameter = new() { Id = Guid.NewGuid(), TemplateId = Guid.NewGuid(), Name = Lanyard.Shared.Projection.ProjectionParameterNames.Dashboard, DataType = "string", IsActive = true };
            ProjectionProgram program = new() { Id = Guid.NewGuid(), Name = "Arena TV", IsActive = true };
            ProjectionProgram retired = new() { Id = Guid.NewGuid(), Name = "Old TV", IsActive = false };
            ProjectionProgramStep step = new() { Id = Guid.NewGuid(), ProjectionProgramId = program.Id, TemplateId = parameter.TemplateId, IsActive = true };
            ProjectionProgramStep retiredStep = new() { Id = Guid.NewGuid(), ProjectionProgramId = retired.Id, TemplateId = parameter.TemplateId, IsActive = true };

            seedCtx.Add(parameter);
            seedCtx.AddRange(program, retired);
            seedCtx.AddRange(step, retiredStep);
            seedCtx.Add(new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = step.Id, ParameterId = parameter.Id, Value = dashboard.Id.ToString() });
            seedCtx.Add(new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = retiredStep.Id, ParameterId = parameter.Id, Value = dashboard.Id.ToString() });
            await seedCtx.SaveChangesAsync();
        }

        Result<IEnumerable<DashboardCard>> result = await service.GetDashboardCardsAsync();

        Assert.IsTrue(result.IsSuccess, result.Error);
        DashboardCard card = result.Data!.Single();
        Assert.AreEqual("Scoreboard TV", card.Name);
        Assert.AreEqual(new DashboardCardWidget(WidgetType.ClientZoneLaserScoreboard, 0, 0, 8, 4), card.Widgets.Single());
        CollectionAssert.AreEqual(new[] { "Arena TV" }, card.ProjectionPrograms.ToArray());
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_KeepsTextTypedOnTheKiosk()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        TextAreaWidget stored = new() { DashboardId = dashboard.Id, Content = "<p>Typed on the kiosk</p>", EditableOnKiosk = true };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(stored);
            await seedCtx.SaveChangesAsync();
        }

        // The designer's copy was loaded before the kiosk edit, so it has the old words.
        dashboard.Widgets = [new TextAreaWidget { Id = stored.Id, Content = "<p>Stale</p>", EditableOnKiosk = true, GridX = 4 }];
        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext ctx = new(options);
        TextAreaWidget saved = await ctx.DashboardWidgets.OfType<TextAreaWidget>().SingleAsync();
        Assert.AreEqual("<p>Typed on the kiosk</p>", saved.Content);
        Assert.AreEqual(4, saved.GridX);
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    public async Task DashboardService_SaveDashboard_SavesTheDesignersTextUnlessTheKioskOwnsIt(bool storedEditable, bool incomingEditable)
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);
        TextAreaWidget stored = new() { DashboardId = dashboard.Id, Content = "<p>Before</p>", EditableOnKiosk = storedEditable };

        await using (ApplicationDbContext seedCtx = new(options))
        {
            seedCtx.DashboardWidgets.Add(stored);
            await seedCtx.SaveChangesAsync();
        }

        dashboard.Widgets = [new TextAreaWidget { Id = stored.Id, Content = "<p>From the designer</p>", EditableOnKiosk = incomingEditable }];
        Result<bool> result = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(result.IsSuccess, result.Error);
        await using ApplicationDbContext ctx = new(options);
        TextAreaWidget saved = await ctx.DashboardWidgets.OfType<TextAreaWidget>().SingleAsync();
        Assert.AreEqual("<p>From the designer</p>", saved.Content);
        Assert.AreEqual(incomingEditable, saved.EditableOnKiosk);
    }

    [TestMethod]
    public async Task DashboardService_SaveDashboard_SavesTheKioskAutoSaveSetting()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        dashboard.Widgets = [new TextAreaWidget { Id = Guid.Empty, EditableOnKiosk = true, KioskAutoSave = true }];
        Result<bool> created = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(created.IsSuccess, created.Error);

        await using (ApplicationDbContext ctx = new(options))
        {
            TextAreaWidget saved = await ctx.DashboardWidgets.OfType<TextAreaWidget>().SingleAsync();
            Assert.IsTrue(saved.KioskAutoSave);
            dashboard.Widgets = [new TextAreaWidget { Id = saved.Id, EditableOnKiosk = true, KioskAutoSave = false }];
        }

        Result<bool> updated = await service.SaveDashboardAsync(dashboard);

        Assert.IsTrue(updated.IsSuccess, updated.Error);
        await using ApplicationDbContext check = new(options);
        Assert.IsFalse((await check.DashboardWidgets.OfType<TextAreaWidget>().SingleAsync()).KioskAutoSave);
    }

    [TestMethod]
    public async Task DashboardService_DeletedDashboard_CantBeLoadedOrSaved()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DashboardService service = GetService(options);
        Dashboard dashboard = await SeedDashboardAsync(options);

        Assert.IsTrue((await service.DeleteDashboardAsync(dashboard.Id)).IsSuccess);

        Result<Dashboard> load = await service.GetDashboardAsync(dashboard.Id);
        dashboard.Name = "Back from the dead";
        dashboard.Widgets = [new GreetingWidget()];
        Result<bool> save = await service.SaveDashboardAsync(dashboard);

        Assert.IsFalse(load.IsSuccess);
        Assert.IsFalse(save.IsSuccess);
        await using ApplicationDbContext ctx = new(options);
        Assert.IsFalse((await ctx.Dashboards.SingleAsync()).IsActive);
        Assert.AreEqual(0, await ctx.DashboardWidgets.CountAsync(x => x.IsActive));
    }
}
