using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class RotaHolidayService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<RotaHolidayService> logger) : IRotaHolidayService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<RotaHolidayService> _logger = logger;

    // Active holidays at a location touching from..to - what Auto-build and the rota read.
    internal static Task<List<RotaHoliday>> ForRangeAsync(ApplicationDbContext ctx, int locationId, DateOnly from, DateOnly to) =>
        ctx.RotaHolidays
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.LocationId == locationId && x.IsActive && x.StartDate <= to && x.EndDate >= from)
            .OrderBy(x => x.StartDate)
            .ToListAsync();

    public async Task<Result<List<RotaHoliday>>> GetHolidaysAsync(LocationScope scope, int locationId, DateOnly? from = null)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<RotaHoliday>>.Fail("You can only see holidays for the location you manage.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<RotaHoliday> holidays = await ctx.RotaHolidays
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId && x.IsActive && (from == null || x.EndDate >= from))
                .OrderBy(x => x.StartDate)
                .ThenBy(x => x.Name)
                .ToListAsync();

            return Result<List<RotaHoliday>>.Ok(holidays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load holidays for {LocationId}", locationId);
            return Result<List<RotaHoliday>>.Fail($"Failed to load holidays: {ex.Message}");
        }
    }

    public async Task<Result<RotaHoliday>> SaveHolidayAsync(LocationScope scope, RotaHoliday holiday, string actingUserId)
    {
        try
        {
            if (RotaHolidayRules.Validate(holiday) is string invalid)
            {
                return Result<RotaHoliday>.Fail(invalid);
            }

            if (!SchedulingAccess.CanManageLocation(scope, holiday.LocationId))
            {
                return Result<RotaHoliday>.Fail("You can only change holidays for the location you manage.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == holiday.LocationId && x.IsActive);

            if (location is null)
            {
                return Result<RotaHoliday>.Fail("Location not found.");
            }

            DateTime now = DateTime.UtcNow;
            RotaHoliday target;

            if (holiday.Id == Guid.Empty)
            {
                target = new RotaHoliday
                {
                    Id = Guid.NewGuid(),
                    CompanyId = location.CompanyId,
                    LocationId = location.Id,
                    Name = string.Empty,
                    CreateDate = now,
                    CreateByUserId = actingUserId
                };

                ctx.RotaHolidays.Add(target);
            }
            else
            {
                RotaHoliday? existing = await ctx.RotaHolidays
                    .FirstOrDefaultAsync(x => x.Id == holiday.Id && x.LocationId == holiday.LocationId && x.IsActive);

                if (existing is null)
                {
                    return Result<RotaHoliday>.Fail("That holiday wasn't found.");
                }

                target = existing;
                target.UpdateDate = now;
                target.UpdateByUserId = actingUserId;
            }

            target.Name = holiday.Name.Trim();
            target.Kind = holiday.Kind;
            target.StartDate = holiday.StartDate;
            target.EndDate = holiday.EndDate;
            target.StaffLike = holiday.Kind == RotaHolidayKind.BankHoliday ? holiday.StaffLike : null;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Rota holiday {HolidayId} ({Kind}) saved for {LocationId} by {ActingUserId}", target.Id, target.Kind, target.LocationId, actingUserId);

            return Result<RotaHoliday>.Ok(target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save holiday for {LocationId}", holiday.LocationId);
            return Result<RotaHoliday>.Fail($"Failed to save the holiday: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveHolidayAsync(LocationScope scope, Guid holidayId, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            RotaHoliday? holiday = await ctx.RotaHolidays.FirstOrDefaultAsync(x => x.Id == holidayId && x.IsActive);

            if (holiday is null || !SchedulingAccess.CanManageLocation(scope, holiday.LocationId))
            {
                return Result<bool>.Fail("That holiday wasn't found.");
            }

            holiday.IsActive = false;
            holiday.UpdateDate = DateTime.UtcNow;
            holiday.UpdateByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Rota holiday {HolidayId} removed by {ActingUserId}", holidayId, actingUserId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove holiday {HolidayId}", holidayId);
            return Result<bool>.Fail($"Failed to remove the holiday: {ex.Message}");
        }
    }

    public async Task<Result<List<RotaHoliday>>> GetMissingBankHolidaysAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<RotaHoliday>>.Fail("You can only change holidays for the location you manage.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            return Result<List<RotaHoliday>>.Ok(await MissingAsync(ctx, locationId, from, to));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to work out bank holidays for {LocationId} from {From} to {To}", locationId, from, to);
            return Result<List<RotaHoliday>>.Fail($"Failed to work out the bank holidays: {ex.Message}");
        }
    }

    public async Task<Result<int>> AddBankHolidaysAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, string actingUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<int>.Fail("You can only change holidays for the location you manage.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == locationId && x.IsActive);

            if (location is null)
            {
                return Result<int>.Fail("Location not found.");
            }

            List<RotaHoliday> missing = await MissingAsync(ctx, locationId, from, to);
            DateTime now = DateTime.UtcNow;

            foreach (RotaHoliday holiday in missing)
            {
                holiday.Id = Guid.NewGuid();
                holiday.CompanyId = location.CompanyId;
                holiday.CreateDate = now;
                holiday.CreateByUserId = actingUserId;
                ctx.RotaHolidays.Add(holiday);
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Added {Count} bank holidays from {From} to {To} at {LocationId} by {ActingUserId}", missing.Count, from, to, locationId, actingUserId);

            return Result<int>.Ok(missing.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add bank holidays for {LocationId} from {From} to {To}", locationId, from, to);
            return Result<int>.Fail($"Failed to add the bank holidays: {ex.Message}");
        }
    }

    // A bank holiday already marked as a bank holiday or closed day (whatever its name) is skipped.
    private static async Task<List<RotaHoliday>> MissingAsync(ApplicationDbContext ctx, int locationId, DateOnly from, DateOnly to)
    {
        if (to < from || to.Year - from.Year > 5)
        {
            return [];
        }

        List<(DateOnly Date, string Name)> days = Enumerable.Range(from.Year, to.Year - from.Year + 1)
            .SelectMany(UkBankHolidays.EnglandAndWales)
            .Where(x => x.Date >= from && x.Date <= to)
            .ToList();

        if (days.Count == 0)
        {
            return [];
        }

        List<RotaHoliday> existing = (await ForRangeAsync(ctx, locationId, from, to))
            .Where(x => x.Kind is RotaHolidayKind.BankHoliday or RotaHolidayKind.Closed)
            .ToList();

        return days
            .Where(d => !existing.Any(x => x.Covers(d.Date)))
            .Select(d => new RotaHoliday
            {
                CompanyId = 0,
                LocationId = locationId,
                Name = d.Name,
                Kind = RotaHolidayKind.BankHoliday,
                StartDate = d.Date,
                EndDate = d.Date,
                StaffLike = DayOfWeek.Sunday
            })
            .ToList();
    }
}
