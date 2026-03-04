using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class DashboardService(IDbContextFactory<ApplicationDbContext> factory) : IDashboardService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    private static readonly HashSet<string> AllowedWidgetTypes =
    [
        "Clock",
        "MusicControls",
        "LaserStats"
    ];

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

    public async Task<Result<Dashboard>> GetDashboardForRenderAsync(Guid dashboardId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dashboard? dashboard = await ctx.Dashboards
                .AsNoTracking()
                .Where(x => x.Id == dashboardId && x.IsActive)
                .Include(x => x.Widgets.Where(w => w.IsActive))
                .FirstOrDefaultAsync();

            if (dashboard is null)
            {
                return Result<Dashboard>.Fail("Dashboard not found.");
            }

            dashboard.Widgets = dashboard.Widgets
                .OrderBy(x => x.SortOrder)
                .ToList();

            return Result<Dashboard>.Ok(dashboard);
        }
        catch (Exception ex)
        {
            return Result<Dashboard>.Fail(ex.Message);
        }
    }

    public async Task<Result<Dashboard>> SaveDashboardAsync(Dashboard dashboard)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dashboard.Name))
            {
                return Result<Dashboard>.Fail("Dashboard name is required.");
            }

            List<DashboardWidget> incomingWidgets = dashboard.Widgets?.ToList() ?? [];
            Result<bool> validationResult = ValidateWidgets(incomingWidgets);
            if (!validationResult.IsSuccess)
            {
                return Result<Dashboard>.Fail(validationResult.Error!);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dashboard? existingDashboard = dashboard.Id == Guid.Empty
                ? null
                : await ctx.Dashboards
                    .Include(x => x.Widgets)
                    .FirstOrDefaultAsync(x => x.Id == dashboard.Id);

            Dashboard targetDashboard;

            if (existingDashboard is null)
            {
                targetDashboard = new Dashboard
                {
                    Id = dashboard.Id == Guid.Empty ? Guid.NewGuid() : dashboard.Id,
                    Name = dashboard.Name.Trim(),
                    Description = dashboard.Description?.Trim(),
                    IsActive = true,
                    CreateDate = DateTime.UtcNow,
                    LastUpdateDate = DateTime.UtcNow,
                    Widgets = []
                };

                ctx.Dashboards.Add(targetDashboard);
            }
            else
            {
                targetDashboard = existingDashboard;
                targetDashboard.Name = dashboard.Name.Trim();
                targetDashboard.Description = dashboard.Description?.Trim();
                targetDashboard.IsActive = true;
                targetDashboard.LastUpdateDate = DateTime.UtcNow;
            }

            Dictionary<Guid, DashboardWidget> existingWidgets = targetDashboard.Widgets.ToDictionary(x => x.Id);
            HashSet<Guid> seenWidgetIds = [];

            for (int i = 0; i < incomingWidgets.Count; i++)
            {
                DashboardWidget incomingWidget = incomingWidgets[i];
                Guid widgetId = incomingWidget.Id == Guid.Empty ? Guid.NewGuid() : incomingWidget.Id;

                if (existingWidgets.TryGetValue(widgetId, out DashboardWidget? existingWidget))
                {
                    existingWidget.Type = incomingWidget.Type;
                    existingWidget.Title = incomingWidget.Title?.Trim();
                    existingWidget.GridX = incomingWidget.GridX;
                    existingWidget.GridY = incomingWidget.GridY;
                    existingWidget.GridW = incomingWidget.GridW;
                    existingWidget.GridH = incomingWidget.GridH;
                    existingWidget.SortOrder = incomingWidget.SortOrder == 0 ? i : incomingWidget.SortOrder;
                    existingWidget.ConfigJson = string.IsNullOrWhiteSpace(incomingWidget.ConfigJson) ? "{}" : incomingWidget.ConfigJson;
                    existingWidget.IsActive = true;
                }
                else
                {
                    DashboardWidget newWidget = new()
                    {
                        Id = widgetId,
                        DashboardId = targetDashboard.Id,
                        Type = incomingWidget.Type,
                        Title = incomingWidget.Title?.Trim(),
                        GridX = incomingWidget.GridX,
                        GridY = incomingWidget.GridY,
                        GridW = incomingWidget.GridW,
                        GridH = incomingWidget.GridH,
                        SortOrder = incomingWidget.SortOrder == 0 ? i : incomingWidget.SortOrder,
                        ConfigJson = string.IsNullOrWhiteSpace(incomingWidget.ConfigJson) ? "{}" : incomingWidget.ConfigJson,
                        IsActive = true
                    };

                    // Explicit add avoids ambiguous tracking state when the key is client-assigned.
                    ctx.DashboardWidgets.Add(newWidget);
                    targetDashboard.Widgets.Add(newWidget);
                }

                seenWidgetIds.Add(widgetId);
            }

            foreach (DashboardWidget existingWidget in targetDashboard.Widgets.Where(x => !seenWidgetIds.Contains(x.Id)))
            {
                existingWidget.IsActive = false;
            }

            await ctx.SaveChangesAsync();

            Dashboard? savedDashboard = await ctx.Dashboards
                .AsNoTracking()
                .Include(x => x.Widgets.Where(w => w.IsActive))
                .FirstOrDefaultAsync(x => x.Id == targetDashboard.Id);

            if (savedDashboard is null)
            {
                return Result<Dashboard>.Fail("Dashboard saved but could not be reloaded.");
            }

            savedDashboard.Widgets = savedDashboard.Widgets.OrderBy(x => x.SortOrder).ToList();

            return Result<Dashboard>.Ok(savedDashboard);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<Dashboard>.Fail("Dashboard changed while saving. Please refresh and try again.");
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
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    private static Result<bool> ValidateWidgets(IEnumerable<DashboardWidget> widgets)
    {
        foreach (DashboardWidget widget in widgets)
        {
            if (!AllowedWidgetTypes.Contains(widget.Type))
            {
                return Result<bool>.Fail($"Unsupported widget type '{widget.Type}'.");
            }

            if (widget.GridX < 0 || widget.GridY < 0 || widget.GridW < 1 || widget.GridH < 1)
            {
                return Result<bool>.Fail("Widget grid values must be positive and non-negative where required.");
            }

            if (widget.GridX + widget.GridW > 12)
            {
                return Result<bool>.Fail("Widget width exceeds dashboard grid bounds.");
            }
        }

        return Result<bool>.Ok(true);
    }
}
