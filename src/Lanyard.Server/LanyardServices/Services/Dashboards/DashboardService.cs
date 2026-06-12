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
}
