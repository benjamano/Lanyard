using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Lanyard.Application.Services.MusicScheduling;

public class MusicScheduleService(
    IDbContextFactory<ApplicationDbContext> factory,
    TimeProvider timeProvider,
    ILogger<MusicScheduleService> logger) : IMusicScheduleService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<MusicScheduleService> _logger = logger;

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task<Result<MusicSchedule>> GetScheduleAsync(LocationScope scope, int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckAccessAsync(ctx, scope, locationId);
            if (accessError is not null)
            {
                return Result<MusicSchedule>.Fail(accessError);
            }

            MusicSchedule? schedule = await ctx.MusicSchedules
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Dayparts.Where(d => d.IsActive))
                    .ThenInclude(d => d.Playlist)
                .FirstOrDefaultAsync(x => x.LocationId == locationId);

            return Result<MusicSchedule>.Ok(schedule ?? new MusicSchedule { LocationId = locationId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the music schedule for location {LocationId}", locationId);
            return Result<MusicSchedule>.Fail($"Failed to load the music schedule: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetEnabledAsync(LocationScope scope, int locationId, bool isEnabled, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckAccessAsync(ctx, scope, locationId);
            if (accessError is not null)
            {
                return Result<bool>.Fail(accessError);
            }

            MusicSchedule schedule = await GetOrAddScheduleAsync(ctx, locationId);

            schedule.IsEnabled = isEnabled;
            schedule.UpdateDate = Now;
            schedule.UpdatedByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("{UserId} turned the music schedule for location {LocationId} {State}", actingUserId, locationId, isEnabled ? "on" : "off");

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to turn the music schedule for location {LocationId} on or off", locationId);
            return Result<bool>.Fail($"Failed to save the music schedule: {ex.Message}");
        }
    }

    public async Task<Result<MusicDaypart>> SaveDaypartAsync(LocationScope scope, int locationId, MusicDaypart daypart, string actingUserId)
    {
        try
        {
            string? problem = MusicDaypartRules.Validate(daypart);
            if (problem is not null)
            {
                return Result<MusicDaypart>.Fail(problem);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckAccessAsync(ctx, scope, locationId);
            if (accessError is not null)
            {
                return Result<MusicDaypart>.Fail(accessError);
            }

            // Playlists are company-filtered, so this also stops another company's playlist.
            if (daypart.PlaylistId is Guid playlistId
                && !await ctx.Playlists.AnyAsync(x => x.Id == playlistId && x.DeleteDate == null))
            {
                return Result<MusicDaypart>.Fail("That playlist no longer exists. Pick another one.");
            }

            MusicSchedule schedule = await GetOrAddScheduleAsync(ctx, locationId);

            List<MusicDaypart> others = await ctx.MusicDayparts
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.MusicScheduleId == schedule.Id && x.IsActive && x.Id != daypart.Id)
                .ToListAsync();

            string? overlap = MusicDaypartRules.FindOverlap(daypart, others);
            if (overlap is not null)
            {
                return Result<MusicDaypart>.Fail(overlap);
            }

            MusicDaypart? row;

            if (daypart.Id == Guid.Empty)
            {
                // Name and days are required members; both are set again below with the rest.
                // Added through the DbSet, not schedule.Dayparts: a new row whose Guid key is
                // already set, reached through a tracked parent's collection, is taken for an
                // existing row and UPDATEd.
                row = new MusicDaypart
                {
                    Id = Guid.CreateVersion7(),
                    MusicScheduleId = schedule.Id,
                    Name = daypart.Name,
                    DaysOfWeek = daypart.DaysOfWeek,
                    CreateDate = Now,
                    CreateByUserId = actingUserId
                };

                ctx.MusicDayparts.Add(row);
            }
            else
            {
                row = await ctx.MusicDayparts.FirstOrDefaultAsync(x => x.Id == daypart.Id && x.MusicScheduleId == schedule.Id && x.IsActive);

                if (row is null)
                {
                    return Result<MusicDaypart>.Fail("That part of the day has been deleted. Refresh the page.");
                }
            }

            row.Name = daypart.Name.Trim();
            row.DaysOfWeek = AutomationScheduleDays.TryParse(daypart.DaysOfWeek, out List<DayOfWeek> days)
                ? AutomationScheduleDays.Serialize(days)
                : daypart.DaysOfWeek;
            row.StartTime = daypart.StartTime;
            row.EndTime = daypart.EndTime;
            row.PlaylistId = daypart.PlaylistId;
            row.Volume = daypart.Volume;
            row.UpdateDate = Now;
            row.UpdatedByUserId = actingUserId;

            schedule.UpdateDate = Now;
            schedule.UpdatedByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("{UserId} saved music daypart {DaypartId} ({Name}) at location {LocationId}", actingUserId, row.Id, row.Name, locationId);

            return Result<MusicDaypart>.Ok(row);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save a music daypart for location {LocationId}", locationId);
            return Result<MusicDaypart>.Fail($"Failed to save: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteDaypartAsync(LocationScope scope, int locationId, Guid daypartId, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckAccessAsync(ctx, scope, locationId);
            if (accessError is not null)
            {
                return Result<bool>.Fail(accessError);
            }

            MusicDaypart? row = await ctx.MusicDayparts
                .FirstOrDefaultAsync(x => x.Id == daypartId && x.MusicSchedule!.LocationId == locationId && x.IsActive);

            if (row is null)
            {
                return Result<bool>.Fail("That part of the day has already been deleted.");
            }

            row.IsActive = false;
            row.UpdateDate = Now;
            row.UpdatedByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("{UserId} deleted music daypart {DaypartId} at location {LocationId}", actingUserId, daypartId, locationId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete music daypart {DaypartId}", daypartId);
            return Result<bool>.Fail($"Failed to delete: {ex.Message}");
        }
    }

    public async Task<Result<List<MusicScheduleKiosk>>> GetLocationKiosksAsync(LocationScope scope, int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckAccessAsync(ctx, scope, locationId);
            if (accessError is not null)
            {
                return Result<List<MusicScheduleKiosk>>.Fail(accessError);
            }

            List<MusicScheduleKiosk> kiosks = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId)
                .OrderBy(x => x.Name)
                .Select(x => new MusicScheduleKiosk(x.Id, x.Name))
                .ToListAsync();

            return Result<List<MusicScheduleKiosk>>.Ok(kiosks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the kiosks at location {LocationId}", locationId);
            return Result<List<MusicScheduleKiosk>>.Fail($"Failed to load the kiosks: {ex.Message}");
        }
    }

    public async Task<Result<List<string>>> GetConflictingAutomationRulesAsync(LocationScope scope, int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckAccessAsync(ctx, scope, locationId);
            if (accessError is not null)
            {
                return Result<List<string>>.Fail(accessError);
            }

            HashSet<Guid> kioskIds = [.. await ctx.Clients
                .AsNoTracking()
                .Where(x => x.LocationId == locationId)
                .Select(x => x.Id)
                .ToListAsync()];

            if (kioskIds.Count == 0)
            {
                return Result<List<string>>.Ok([]);
            }

            var musicActions = await ctx.AutomationRuleActions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive
                    && x.ActionType == AutomationActionTypes.MusicControl
                    && x.AutomationRule!.IsActive
                    && x.AutomationRule.IsEnabled)
                .Select(x => new { x.ParametersJson, RuleName = x.AutomationRule!.Name })
                .ToListAsync();

            List<string> names = musicActions
                .Where(x => TargetClientId(x.ParametersJson) is Guid target && kioskIds.Contains(target))
                .Select(x => x.RuleName)
                .Distinct()
                .Order()
                .ToList();

            return Result<List<string>>.Ok(names);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check automation rules for location {LocationId}", locationId);
            return Result<List<string>>.Fail($"Failed to check automation rules: {ex.Message}");
        }
    }

    public async Task<Result<Dictionary<Guid, int>>> GetPlaylistSongCountsAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dictionary<Guid, int> counts = await ctx.PlaylistSongMembers
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.DeleteDate == null && x.Song!.IsActive && x.Playlist!.DeleteDate == null)
                .GroupBy(x => x.PlaylistId)
                .Select(g => new { PlaylistId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.PlaylistId, x => x.Count);

            return Result<Dictionary<Guid, int>>.Ok(counts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to count playlist songs");
            return Result<Dictionary<Guid, int>>.Fail($"Failed to count playlist songs: {ex.Message}");
        }
    }

    // The same rule as the rota (Admin anywhere, Manager at their own location), plus a company
    // check: Locations have no tenant filter, and nothing else stops an Admin naming another
    // company's location id when a schedule row is first created.
    private static async Task<string?> CheckAccessAsync(ApplicationDbContext ctx, LocationScope scope, int locationId)
    {
        if (!SchedulingAccess.CanManageLocation(scope, locationId))
        {
            return "You can only change the music schedule for your own location.";
        }

        if (scope.CompanyId is not int companyId)
        {
            return "Couldn't work out which company you're signed in to.";
        }

        bool isCompanyLocation = await ctx.Locations
            .AsNoTracking()
            .AnyAsync(x => x.Id == locationId && x.CompanyId == companyId && x.IsActive);

        return isCompanyLocation ? null : "That location doesn't exist.";
    }

    private static async Task<MusicSchedule> GetOrAddScheduleAsync(ApplicationDbContext ctx, int locationId)
    {
        MusicSchedule? schedule = await ctx.MusicSchedules.FirstOrDefaultAsync(x => x.LocationId == locationId);

        if (schedule is null)
        {
            schedule = new MusicSchedule { Id = Guid.CreateVersion7(), LocationId = locationId };
            ctx.MusicSchedules.Add(schedule);
        }

        return schedule;
    }

    private static Guid? TargetClientId(string parametersJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(parametersJson);

            return doc.RootElement.TryGetProperty("TargetClientId", out JsonElement value) && value.TryGetGuid(out Guid id)
                ? id
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
