using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.AudioClips;

public class AudioClipScheduleService(
    IDbContextFactory<ApplicationDbContext> factory,
    IAudioClipPlaybackService playback,
    TimeProvider timeProvider,
    ILogger<AudioClipScheduleService> logger) : IAudioClipScheduleService
{
    public async Task<Result<List<AudioClipScheduleSummary>>> ListAsync(LocationScope scope)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            IQueryable<AudioClipSchedule> query = ctx.AudioClipSchedules
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.AudioClip)
                .Include(x => x.Location)
                .Where(x => x.DeleteDate == null);

            if (!scope.IsAdmin)
            {
                query = query.Where(x => x.LocationId == scope.LocationId);
            }

            List<AudioClipSchedule> schedules = await query.OrderBy(x => x.Location!.Name).ThenBy(x => x.Name).ToListAsync();

            List<int> locationIds = schedules.Select(x => x.LocationId).Distinct().ToList();
            Dictionary<int, List<LocationOpeningHours>> hours = (await ctx.LocationOpeningHours
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(x => locationIds.Contains(x.LocationId) && x.IsActive)
                    .ToListAsync())
                .GroupBy(x => x.LocationId)
                .ToDictionary(x => x.Key, x => x.ToList());

            DateTime venueLocal = RotaTime.ToVenueLocal(timeProvider.GetUtcNow().UtcDateTime);

            return Result<List<AudioClipScheduleSummary>>.Ok(schedules
                .Select(x =>
                {
                    List<LocationOpeningHours> windows = hours.GetValueOrDefault(x.LocationId) ?? [];
                    return new AudioClipScheduleSummary(x, AudioClipScheduleRules.Upcoming(x, windows, venueLocal), windows.Count > 0);
                })
                .ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list audio clip schedules");
            return Result<List<AudioClipScheduleSummary>>.Fail($"Failed to load the schedules: {ex.Message}");
        }
    }

    public async Task<Result<AudioClipSchedule>> SaveAsync(LocationScope scope, AudioClipSchedule schedule, string userId)
    {
        try
        {
            if (AudioClipScheduleRules.Validate(schedule) is string invalid)
            {
                return Result<AudioClipSchedule>.Fail(invalid);
            }

            if (!SchedulingAccess.CanManageLocation(scope, schedule.LocationId))
            {
                return Result<AudioClipSchedule>.Fail("You can only schedule clips for your own location.");
            }

            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            if (!await ctx.Locations.AnyAsync(x => x.Id == schedule.LocationId && x.IsActive))
            {
                return Result<AudioClipSchedule>.Fail("That location doesn't exist.");
            }

            AudioClip? clip = await ctx.AudioClips.AsNoTracking().FirstOrDefaultAsync(x => x.Id == schedule.AudioClipId && x.DeleteDate == null);

            if (clip is null)
            {
                return Result<AudioClipSchedule>.Fail("Pick a clip.");
            }

            if (clip.LocationId is int clipLocation && clipLocation != schedule.LocationId)
            {
                return Result<AudioClipSchedule>.Fail("That clip only plays at another location.");
            }

            DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
            AudioClipSchedule saved;

            if (schedule.Id == Guid.Empty)
            {
                saved = new AudioClipSchedule
                {
                    Id = Guid.CreateVersion7(),
                    CreateDate = utcNow,
                    CreatedByUserId = userId,
                    IsEnabled = true
                };

                ctx.AudioClipSchedules.Add(saved);
            }
            else
            {
                AudioClipSchedule? existing = await ctx.AudioClipSchedules.FirstOrDefaultAsync(x => x.Id == schedule.Id && x.DeleteDate == null);

                if (existing is null)
                {
                    return Result<AudioClipSchedule>.Fail("Schedule not found.");
                }

                // Moving a schedule needs both locations to be the caller's.
                if (!SchedulingAccess.CanManageLocation(scope, existing.LocationId))
                {
                    return Result<AudioClipSchedule>.Fail("You can only schedule clips for your own location.");
                }

                saved = existing;
                saved.UpdateDate = utcNow;
            }

            saved.Name = schedule.Name.Trim();
            saved.AudioClipId = schedule.AudioClipId;
            saved.LocationId = schedule.LocationId;
            saved.Anchor = schedule.Anchor;
            saved.TimeOfDay = schedule.Anchor == AudioClipScheduleAnchor.AtTime && schedule.TimeOfDay is TimeOnly time
                ? new TimeOnly(time.Hour, time.Minute)
                : null;
            saved.OffsetMinutes = schedule.Anchor == AudioClipScheduleAnchor.AtTime ? 0 : schedule.OffsetMinutes;
            saved.DaysOfWeek = schedule.DaysOfWeek;

            await ctx.SaveChangesAsync();

            logger.LogInformation("Audio clip schedule {ScheduleId} saved: {Description} at location {LocationId}", saved.Id, AudioClipScheduleRules.Describe(saved), saved.LocationId);

            await playback.PrefetchScheduledClipsAsync(saved.LocationId);

            return Result<AudioClipSchedule>.Ok(saved);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save audio clip schedule {ScheduleId}", schedule.Id);
            return Result<AudioClipSchedule>.Fail($"Failed to save the schedule: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetEnabledAsync(LocationScope scope, Guid scheduleId, bool isEnabled)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            AudioClipSchedule? schedule = await ctx.AudioClipSchedules.FirstOrDefaultAsync(x => x.Id == scheduleId && x.DeleteDate == null);

            if (schedule is null)
            {
                return Result<bool>.Fail("Schedule not found.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, schedule.LocationId))
            {
                return Result<bool>.Fail("You can only schedule clips for your own location.");
            }

            schedule.IsEnabled = isEnabled;
            schedule.UpdateDate = timeProvider.GetUtcNow().UtcDateTime;
            await ctx.SaveChangesAsync();

            logger.LogInformation("Audio clip schedule {ScheduleId} turned {State}", schedule.Id, isEnabled ? "on" : "off");

            if (isEnabled)
            {
                await playback.PrefetchScheduledClipsAsync(schedule.LocationId);
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to switch audio clip schedule {ScheduleId}", scheduleId);
            return Result<bool>.Fail($"Failed to save the schedule: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteAsync(LocationScope scope, Guid scheduleId)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            AudioClipSchedule? schedule = await ctx.AudioClipSchedules.FirstOrDefaultAsync(x => x.Id == scheduleId && x.DeleteDate == null);

            if (schedule is null)
            {
                return Result<bool>.Fail("Schedule not found.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, schedule.LocationId))
            {
                return Result<bool>.Fail("You can only schedule clips for your own location.");
            }

            schedule.DeleteDate = timeProvider.GetUtcNow().UtcDateTime;
            await ctx.SaveChangesAsync();

            logger.LogInformation("Audio clip schedule {ScheduleId} deleted", schedule.Id);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete audio clip schedule {ScheduleId}", scheduleId);
            return Result<bool>.Fail($"Failed to delete the schedule: {ex.Message}");
        }
    }

    public async Task<Result<bool>> LocationHasHoursAsync(int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            return Result<bool>.Ok(await ctx.LocationOpeningHours.AsNoTracking().AnyAsync(x => x.LocationId == locationId && x.IsActive));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check the opening hours of location {LocationId}", locationId);
            return Result<bool>.Fail($"Failed to check the opening hours: {ex.Message}");
        }
    }
}
