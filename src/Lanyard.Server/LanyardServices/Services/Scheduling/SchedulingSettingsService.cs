using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
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
            existing.SendShiftReminders = settings.SendShiftReminders;
            existing.ClockInWindowMinutes = settings.ClockInWindowMinutes;
            existing.PayPeriod = settings.PayPeriod;
            existing.PayPeriodStartDay = settings.PayPeriodStartDay;
            existing.PayPeriodAnchorDate = settings.PayPeriod == PayPeriodFrequency.Monthly ? null : settings.PayPeriodAnchorDate;
            existing.BreakDeduction = settings.BreakDeduction;
            existing.BreakDeductionMinutes = settings.BreakDeductionMinutes;
            existing.BreakDeductionAfterHours = settings.BreakDeductionAfterHours;
            existing.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            return Result<CompanySchedulingSettings>.Ok(existing);
        }
        catch (Exception ex)
        {
            return Result<CompanySchedulingSettings>.Fail($"Failed to save scheduling settings: {ex.Message}");
        }
    }

    private static string? Validate(CompanySchedulingSettings settings)
    {
        if (settings.FinancialYearStartMonth is < 1 or > 12)
        {
            return "Financial year start month must be between 1 and 12.";
        }

        // Validated against a non-leap year on purpose: a holiday year that starts on 29 February
        // has no start date three years in four, and the allowance rollover would have to invent
        // one. 28 February is the latest February start allowed.
        int daysInMonth = DateTime.DaysInMonth(2023, settings.FinancialYearStartMonth);

        if (settings.FinancialYearStartDay < 1 || settings.FinancialYearStartDay > daysInMonth)
        {
            return $"Financial year start day must be between 1 and {daysInMonth} for that month.";
        }

        if (settings.HoursPerDay is <= 0 or > 24)
        {
            return "Hours per day must be greater than 0 and at most 24.";
        }

        if (settings.ShiftReminderLeadHours is < 1 or > 168)
        {
            return "Shift reminder lead time must be between 1 and 168 hours.";
        }

        if (settings.ClockInWindowMinutes is < 0 or > 240)
        {
            return "The clock-in window must be between 0 and 240 minutes.";
        }

        if (!System.Enum.IsDefined(settings.PayPeriod))
        {
            return "Choose how often you run payroll.";
        }

        // Capped at 28 so every month, February included, has the day a period starts on.
        if (settings.PayPeriod == PayPeriodFrequency.Monthly && settings.PayPeriodStartDay is < 1 or > 28)
        {
            return "A monthly pay period must start on a day between 1 and 28.";
        }

        if (settings.PayPeriod != PayPeriodFrequency.Monthly && settings.PayPeriodAnchorDate is null)
        {
            return "Choose the first day of any one pay period, so Lanyard can count the periods from it.";
        }

        if (!System.Enum.IsDefined(settings.BreakDeduction))
        {
            return "Choose how breaks are handled in the payroll export.";
        }

        if (settings.BreakDeduction == BreakDeductionRule.FixedOverThreshold)
        {
            if (settings.BreakDeductionMinutes is < 1 or > 120)
            {
                return "The break to deduct must be between 1 and 120 minutes.";
            }

            if (settings.BreakDeductionAfterHours is <= 0 or > 24)
            {
                return "The hours before a break is deducted must be greater than 0 and at most 24.";
            }
        }

        return null;
    }
}
