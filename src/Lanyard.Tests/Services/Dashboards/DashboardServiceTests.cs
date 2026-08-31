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

        Result<Dashboard> dashboardResult = await service.GetDashboardAsync(dashboard.Id);

        Assert.IsTrue(dashboardResult.Success, dashboardResult.Error);
        Assert.IsFalse(dashboardResult.Data!.IsActive);
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
}
