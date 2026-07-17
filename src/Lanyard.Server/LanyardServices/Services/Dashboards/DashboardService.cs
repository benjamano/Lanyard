using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class DashboardService(IDbContextFactory<ApplicationDbContext> factory) : IDashboardService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<IEnumerable<Dashboard>>> GetDashboardsAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<Dashboard> dashboards = await ctx.Dashboards
                .AsNoTracking()
                .Where(x => x.IsActive)
                .Include(x => x.Widgets.Where(w => w.IsActive))
                .OrderBy(x => x.Name)
                .ToListAsync();

            return Result<IEnumerable<Dashboard>>.Ok(dashboards);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<Dashboard>>.Fail(ex.Message);
        }
    }

    public async Task<Result<Dashboard>> GetDashboardAsync(Guid dashboardId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dashboard? dashboard = await ctx.Dashboards
                .AsNoTracking()
                .Include(x => x.Widgets)
                .FirstOrDefaultAsync(x => x.Id == dashboardId);

            if (dashboard is null)
            {
                return Result<Dashboard>.Fail("Dashboard not found.");
            }

            return Result<Dashboard>.Ok(dashboard);
        }
        catch (Exception ex)
        {
            return Result<Dashboard>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> DeleteDashboardAsync(Guid dashboardId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dashboard? dashboard = await ctx.Dashboards
                .Include(x => x.Widgets)
                .FirstOrDefaultAsync(x => x.Id == dashboardId);

            if (dashboard is null)
            {
                return Result<bool>.Fail("Dashboard not found.");
            }

            dashboard.IsActive = false;
            dashboard.LastUpdateDate = DateTime.UtcNow;

            foreach (DashboardWidget widget in dashboard.Widgets)
            {
                widget.IsActive = false;
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<bool>.Fail("The dashboard was modified by another operation. Please reload and try again.");
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> CreateDashboardAsync(Dashboard dashboard)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dashboard.Name))
            {
                return Result<bool>.Fail("Dashboard name is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dashboard newDashboard = new()
            {
                Name = dashboard.Name.Trim(),
                Description = dashboard.Description?.Trim(),
                IsActive = true,
                CreateDate = DateTime.UtcNow,
                LastUpdateDate = DateTime.UtcNow
            };

            ctx.Dashboards.Add(newDashboard);
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SaveDashboardAsync(Dashboard dashboard)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(dashboard);

            if (dashboard.Id == Guid.Empty)
            {
                return Result<bool>.Fail("Dashboard id is required.");
            }

            if (string.IsNullOrWhiteSpace(dashboard.Name))
            {
                return Result<bool>.Fail("Dashboard name is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dashboard? existingDashboard = await ctx.Dashboards
                .FirstOrDefaultAsync(x => x.Id == dashboard.Id);

            if (existingDashboard is null)
            {
                return Result<bool>.Fail("Dashboard not found.");
            }

            existingDashboard.Name = dashboard.Name.Trim();
            existingDashboard.Description = dashboard.Description?.Trim();
            existingDashboard.LastUpdateDate = DateTime.UtcNow;

            List<DashboardWidget> incomingWidgets = dashboard.Widgets ?? [];
            List<DashboardWidget> existingWidgets = await ctx.DashboardWidgets
                .Where(x => x.DashboardId == dashboard.Id)
                .ToListAsync();

            Dictionary<Guid, DashboardWidget> existingWidgetsById = existingWidgets
                .ToDictionary(x => x.Id, x => x);

            foreach (DashboardWidget incomingWidget in incomingWidgets)
            {
                if (incomingWidget.Id == Guid.Empty)
                {
                    incomingWidget.Id = Guid.NewGuid();
                }

                DashboardWidget? existingWidget = existingWidgetsById
                    .GetValueOrDefault(incomingWidget.Id);

                if (existingWidget is null)
                {
                    DashboardWidget newWidget = CreateWidgetCopy(incomingWidget, existingDashboard.Id);
                    await ctx.DashboardWidgets.AddAsync(newWidget);
                    continue;
                }

                if (existingWidget.GetType() != incomingWidget.GetType())
                {
                    return Result<bool>.Fail("Widget type mismatch.");
                }

                UpdateCommonMutableWidgetProperties(existingWidget, incomingWidget);
                UpdateTypeSpecificWidgetProperties(existingWidget, incomingWidget);
            }

            HashSet<Guid> incomingWidgetIds = incomingWidgets.Select(x => x.Id).ToHashSet();

            foreach (DashboardWidget existingWidget in existingWidgets)
            {
                if (!incomingWidgetIds.Contains(existingWidget.Id))
                {
                    existingWidget.IsActive = false;
                }
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<bool>.Fail("Dashboard was changed by another operation. Refresh and try saving again.");
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<DashboardWidget>> SaveWidgetAsync(DashboardWidget widget)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(widget);

            if (widget.Id == Guid.Empty)
            {
                return Result<DashboardWidget>.Fail("Widget id is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            DashboardWidget? existingWidget = await ctx.DashboardWidgets
                .FirstOrDefaultAsync(x => x.Id == widget.Id);

            if (existingWidget is null)
            {
                return Result<DashboardWidget>.Fail("Widget not found.");
            }

            existingWidget.Title = widget.Title?.Trim();
            existingWidget.GridX = widget.GridX;
            existingWidget.GridY = widget.GridY;
            existingWidget.GridW = widget.GridW;
            existingWidget.GridH = widget.GridH;
            existingWidget.IsActive = widget.IsActive;

            if (existingWidget.GetType() != widget.GetType())
            {
                return Result<DashboardWidget>.Fail("Widget type mismatch.");
            }

            switch (existingWidget)
            {
                case TextAreaWidget existingTextArea when widget is TextAreaWidget incomingTextArea:
                    existingTextArea.Content = incomingTextArea.Content;
                    break;

                case DigitalClockWidget existingClock when widget is DigitalClockWidget incomingClock:
                    existingClock.ShowDate = incomingClock.ShowDate;
                    existingClock.ShowMilliSeconds = incomingClock.ShowMilliSeconds;
                    existingClock.Is24HourFormat = incomingClock.Is24HourFormat;
                    break;
                case ClientZoneLaserScoreboardWidget existingScoreboard when widget is ClientZoneLaserScoreboardWidget incomingScoreboard:
                    existingScoreboard.ClientId = incomingScoreboard.ClientId;
                    break;
                case ClientZoneLaserGameStatusWidget existingLaserGameStatus when widget is ClientZoneLaserGameStatusWidget incomingLaserGameStatus:
                    existingLaserGameStatus.ShowCurrentGameStatus = incomingLaserGameStatus.ShowCurrentGameStatus;
                    existingLaserGameStatus.ShowTimeLeft = incomingLaserGameStatus.ShowTimeLeft;
                    existingLaserGameStatus.ClientId = incomingLaserGameStatus.ClientId;
                    break;
                case ButtonWidget existingButton when widget is ButtonWidget incomingButton:
                    existingButton.Label = incomingButton.Label;
                    existingButton.Appearance = incomingButton.Appearance;
                    existingButton.ActionType = incomingButton.ActionType;
                    existingButton.ClientId = incomingButton.ClientId;
                    existingButton.ProjectionProgramId = incomingButton.ProjectionProgramId;
                    existingButton.DisplayIndex = incomingButton.DisplayIndex;
                    break;
                case MusicPlaylistSelectorWidget existingPlaylistSelector when widget is MusicPlaylistSelectorWidget incomingPlaylistSelector:
                    existingPlaylistSelector.ClientId = incomingPlaylistSelector.ClientId;
                    break;
                case MusicTimelineWidget existingTimeline when widget is MusicTimelineWidget incomingTimeline:
                    existingTimeline.ClientId = incomingTimeline.ClientId;
                    existingTimeline.ShowSongTitle = incomingTimeline.ShowSongTitle;
                    break;
            }

            Dashboard? parentDashboard = await ctx.Dashboards
                .FirstOrDefaultAsync(x => x.Id == existingWidget.DashboardId);

            if (parentDashboard is not null)
            {
                parentDashboard.LastUpdateDate = DateTime.UtcNow;
            }

            await ctx.SaveChangesAsync();

            return Result<DashboardWidget>.Ok(existingWidget);
        }
        catch (Exception ex)
        {
            return Result<DashboardWidget>.Fail(ex.Message);
        }
    }

    private static DashboardWidget CreateWidgetCopy(DashboardWidget widget, Guid dashboardId)
    {
        DashboardWidget copy = widget switch
        {
            TextAreaWidget textAreaWidget => new TextAreaWidget
            {
                Content = textAreaWidget.Content
            },
            DigitalClockWidget clockWidget => new DigitalClockWidget
            {
                ShowDate = clockWidget.ShowDate,
                ShowMilliSeconds = clockWidget.ShowMilliSeconds,
                Is24HourFormat = clockWidget.Is24HourFormat
            },
            ClientZoneLaserGameStatusWidget laserGameStatusWidget => new ClientZoneLaserGameStatusWidget
            {
                ShowCurrentGameStatus = laserGameStatusWidget.ShowCurrentGameStatus,
                ShowTimeLeft = laserGameStatusWidget.ShowTimeLeft,
                ClientId = laserGameStatusWidget.ClientId
            },
            ClientZoneLaserScoreboardWidget scoreboardWidget => new ClientZoneLaserScoreboardWidget
            {
                ClientId = scoreboardWidget.ClientId
            },
            ButtonWidget buttonWidget => new ButtonWidget
            {
                Label = buttonWidget.Label,
                Appearance = buttonWidget.Appearance,
                ActionType = buttonWidget.ActionType,
                ClientId = buttonWidget.ClientId,
                ProjectionProgramId = buttonWidget.ProjectionProgramId,
                DisplayIndex = buttonWidget.DisplayIndex
            },
            MusicPlaylistSelectorWidget playlistSelectorWidget => new MusicPlaylistSelectorWidget
            {
                ClientId = playlistSelectorWidget.ClientId
            },
            MusicTimelineWidget timelineWidget => new MusicTimelineWidget
            {
                ClientId = timelineWidget.ClientId,
                ShowSongTitle = timelineWidget.ShowSongTitle
            },
            _ => throw new InvalidOperationException("Unsupported widget type.")
        };

        copy.Id = widget.Id == Guid.Empty ? Guid.NewGuid() : widget.Id;
        copy.Type = widget.Type;
        UpdateCommonMutableWidgetProperties(copy, widget);
        copy.DashboardId = dashboardId;

        return copy;
    }

    private static void UpdateCommonMutableWidgetProperties(DashboardWidget target, DashboardWidget source)
    {
        target.Title = source.Title?.Trim();
        target.GridX = source.GridX;
        target.GridY = source.GridY;
        target.GridW = source.GridW;
        target.GridH = source.GridH;
        target.IsActive = source.IsActive;
    }

    private static void UpdateTypeSpecificWidgetProperties(DashboardWidget target, DashboardWidget source)
    {
        if (target is TextAreaWidget targetTextArea && source is TextAreaWidget sourceTextArea)
        {
            targetTextArea.Content = sourceTextArea.Content;
            return;
        }

        if (target is DigitalClockWidget targetClock && source is DigitalClockWidget sourceClock)
        {
            targetClock.ShowDate = sourceClock.ShowDate;
            targetClock.ShowMilliSeconds = sourceClock.ShowMilliSeconds;
            targetClock.Is24HourFormat = sourceClock.Is24HourFormat;
        }

        if (target is ClientZoneLaserGameStatusWidget targetLaserGameStatus && source is ClientZoneLaserGameStatusWidget sourceLaserGameStatus)
        {
            targetLaserGameStatus.ShowCurrentGameStatus = sourceLaserGameStatus.ShowCurrentGameStatus;
            targetLaserGameStatus.ShowTimeLeft = sourceLaserGameStatus.ShowTimeLeft;
            targetLaserGameStatus.ClientId = sourceLaserGameStatus.ClientId;
        }

        if (target is ClientZoneLaserScoreboardWidget targetScoreboard && source is ClientZoneLaserScoreboardWidget sourceScoreboard)
        {
            targetScoreboard.ClientId = sourceScoreboard.ClientId;
        }

        if (target is ButtonWidget targetButton && source is ButtonWidget sourceButton)
        {
            targetButton.Label = sourceButton.Label;
            targetButton.Appearance = sourceButton.Appearance;
            targetButton.ActionType = sourceButton.ActionType;
            targetButton.ClientId = sourceButton.ClientId;
            targetButton.ProjectionProgramId = sourceButton.ProjectionProgramId;
            targetButton.DisplayIndex = sourceButton.DisplayIndex;
        }

        if (target is MusicPlaylistSelectorWidget targetPlaylistSelector && source is MusicPlaylistSelectorWidget sourcePlaylistSelector)
        {
            targetPlaylistSelector.ClientId = sourcePlaylistSelector.ClientId;
        }

        if (target is MusicTimelineWidget targetTimeline && source is MusicTimelineWidget sourceTimeline)
        {
            targetTimeline.ClientId = sourceTimeline.ClientId;
            targetTimeline.ShowSongTitle = sourceTimeline.ShowSongTitle;
        }
    }
}
