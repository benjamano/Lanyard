using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

public class SchedulingSettingsService(IDbContextFactory<ApplicationDbContext> factory) : ISchedulingSettingsService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<CompanySchedulingSettings>> GetSettingsAsync(int companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CompanySchedulingSettings? settings = await ctx.CompanySchedulingSettings
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.CompanyId == companyId);

            return Result<CompanySchedulingSettings>.Ok(settings ?? new CompanySchedulingSettings { CompanyId = companyId });
        }
        catch (Exception ex)
        {
            return Result<CompanySchedulingSettings>.Fail($"Failed to retrieve scheduling settings: {ex.Message}");
        }
    }

    public async Task<Result<CompanySchedulingSettings>> SaveSettingsAsync(LocationScope scope, CompanySchedulingSettings settings)
    {
        try
        {
            if (!scope.IsAdmin && scope.CompanyId != settings.CompanyId)
            {
                return Result<CompanySchedulingSettings>.Fail("You can only edit scheduling settings for your own company.");
            }

            string? validationError = Validate(settings);

            if (validationError is not null)
            {
                return Result<CompanySchedulingSettings>.Fail(validationError);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CompanySchedulingSettings? existing = await ctx.CompanySchedulingSettings
                .FirstOrDefaultAsync(x => x.CompanyId == settings.CompanyId);

            if (existing is null)
            {
                existing = new CompanySchedulingSettings { Id = Guid.NewGuid(), CompanyId = settings.CompanyId };
                ctx.CompanySchedulingSettings.Add(existing);
            }

            existing.FinancialYearStartMonth = settings.FinancialYearStartMonth;
            existing.FinancialYearStartDay = settings.FinancialYearStartDay;
            existing.HoursPerDay = settings.HoursPerDay;
            existing.ShiftReminderLeadHours = settings.ShiftReminderLeadHours;
            existing.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            return Result<CompanySchedulingSettings>.Ok(existing);
        }
        catch (Exception ex)
        {
            return Result<CompanySchedulingSettings>.Fail($"Failed to save scheduling settings: {ex.Message}");
        }
    }

    public async Task<Result<int>> ResolveCompanyIdForUserAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<int> companyIds = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == userId && x.Location!.IsActive && x.Location.Company!.IsActive)
                .Select(x => x.Location!.CompanyId)
                .Distinct()
                .ToListAsync();

            if (companyIds.Count == 0)
            {
                return Result<int>.Fail("This user doesn't belong to an active company yet - add them to a location first.");
            }

            if (companyIds.Count > 1)
            {
                return Result<int>.Fail("This user belongs to more than one company, so their rota settings can't be resolved.");
            }

            return Result<int>.Ok(companyIds[0]);
        }
        catch (Exception ex)
        {
            return Result<int>.Fail($"Failed to resolve the user's company: {ex.Message}");
        }
    }

    private static string? Validate(CompanySchedulingSettings settings)
    {
        if (settings.FinancialYearStartMonth is < 1 or > 12)
        {
            return "Financial year start month must be between 1 and 12.";
        }

        // 2024 is a leap year, so 29 February is accepted as a start day.
        int daysInMonth = DateTime.DaysInMonth(2024, settings.FinancialYearStartMonth);

        if (settings.FinancialYearStartDay < 1 || settings.FinancialYearStartDay > daysInMonth)
        {
            return $"Financial year start day must be between 1 and {daysInMonth} for that month.";
        }

        if (settings.HoursPerDay is <= 0 or > 24)
        {
            return "Hours per day must be between 0 and 24.";
        }

        if (settings.ShiftReminderLeadHours is < 1 or > 168)
        {
            return "Shift reminder lead time must be between 1 and 168 hours.";
        }

        return null;
    }
}
