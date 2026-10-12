using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Announcements;

public class AnnouncementService(
    IDbContextFactory<ApplicationDbContext> factory,
    INotificationDispatcher notifications,
    ILogger<AnnouncementService> logger) : IAnnouncementService
{
    private const int MaxTitleLength = 120;
    private const int MaxBodyLength = 2000;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly INotificationDispatcher _notifications = notifications;
    private readonly ILogger<AnnouncementService> _logger = logger;

    public async Task<Result<List<Announcement>>> GetAnnouncementsAsync(LocationScope scope, bool allLocations)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<Announcement> query = ctx.Announcements
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                    .ThenInclude(x => x!.Company)
                .Where(x => x.IsActive);

            // Mirrors CourseService.GetCoursesAsync: the filter applies to an Admin too, and
            // lifting it is an explicit choice via the "Show all locations" switch. An Admin with
            // no location claim has nothing to filter by, so they still see everything.
            bool applyLocationFilter = !scope.IsAdmin || (!allLocations && scope.LocationId is not null);

            if (applyLocationFilter)
            {
                query = query.Where(x => x.LocationId == scope.LocationId);
            }

            List<Announcement> announcements = await query
                .OrderByDescending(x => x.IsPinned)
                .ThenByDescending(x => x.CreateDate)
                .ToListAsync();

            return Result<List<Announcement>>.Ok(announcements);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve announcements for location {LocationId}", scope.LocationId);

            return Result<List<Announcement>>.Fail($"Failed to retrieve announcements: {ex.Message}");
        }
    }

    public async Task<Result<List<Announcement>>> GetActiveAnnouncementsAsync(LocationScope scope, int maxItems)
    {
        try
        {
            // A viewer with no location resolves to nothing rather than to everything - the widget
            // renders on an anonymous kiosk, and a wall-mounted screen must not fall back to
            // showing every site's notices.
            if (scope.LocationId is null)
            {
                return Result<List<Announcement>>.Ok([]);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            DateTime nowUtc = DateTime.UtcNow;

            List<Announcement> announcements = await ctx.Announcements
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive
                    && x.LocationId == scope.LocationId
                    && (x.ExpiryDate == null || x.ExpiryDate > nowUtc))
                .OrderByDescending(x => x.IsPinned)
                .ThenByDescending(x => x.CreateDate)
                .Take(Math.Clamp(maxItems, 1, AnnouncementsWidget.MaxSupportedItems))
                .ToListAsync();

            return Result<List<Announcement>>.Ok(announcements);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve active announcements for location {LocationId}", scope.LocationId);

            return Result<List<Announcement>>.Fail($"Failed to retrieve announcements: {ex.Message}");
        }
    }

    public async Task<Result<Announcement>> SaveAnnouncementAsync(Announcement announcement, LocationScope scope)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(announcement.Title))
            {
                return Result<Announcement>.Fail("Announcement title is required.");
            }

            if (string.IsNullOrWhiteSpace(announcement.Body))
            {
                return Result<Announcement>.Fail("Announcement body is required.");
            }

            if (announcement.Title.Trim().Length > MaxTitleLength)
            {
                return Result<Announcement>.Fail($"Announcement title cannot be longer than {MaxTitleLength} characters.");
            }

            if (announcement.Body.Trim().Length > MaxBodyLength)
            {
                return Result<Announcement>.Fail($"Announcement body cannot be longer than {MaxBodyLength} characters.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Announcement? existing = announcement.Id == Guid.Empty
                ? null
                : await ctx.Announcements.FirstOrDefaultAsync(x => x.Id == announcement.Id);

            if (existing is not null && !scope.IsAdmin && existing.LocationId != scope.LocationId)
            {
                return Result<Announcement>.Fail("You do not have access to this announcement.");
            }

            // An Admin posts to whichever location the dialog picked; everyone else is pinned to
            // their own, whatever the incoming object claims.
            int? targetLocationId = scope.IsAdmin ? announcement.LocationId : scope.LocationId;

            if (targetLocationId is null)
            {
                return Result<Announcement>.Fail("An announcement must be assigned to a location.");
            }

            if (!scope.IsAdmin && existing is null && scope.LocationId is null)
            {
                return Result<Announcement>.Fail("You do not have access to create announcements.");
            }

            Announcement target;

            if (existing is null)
            {
                target = new Announcement
                {
                    Id = announcement.Id == Guid.Empty ? Guid.NewGuid() : announcement.Id,
                    Title = announcement.Title.Trim(),
                    Body = announcement.Body.Trim(),
                    IsPinned = announcement.IsPinned,
                    LocationId = targetLocationId,
                    ExpiryDate = announcement.ExpiryDate,
                    IsActive = true,
                    CreateDate = DateTime.UtcNow,
                    CreatedByUserId = announcement.CreatedByUserId
                };

                ctx.Announcements.Add(target);
            }
            else
            {
                target = existing;

                target.Title = announcement.Title.Trim();
                target.Body = announcement.Body.Trim();
                target.IsPinned = announcement.IsPinned;
                target.LocationId = targetLocationId;
                target.ExpiryDate = announcement.ExpiryDate;
                target.LastUpdateDate = DateTime.UtcNow;
            }

            await ctx.SaveChangesAsync();

            return Result<Announcement>.Ok(target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save announcement {AnnouncementId}", announcement.Id);

            return Result<Announcement>.Fail($"Failed to save announcement: {ex.Message}");
        }
    }

    public async Task<Result<int>> SendAnnouncementAsync(Guid announcementId, LocationScope scope, string? senderUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Announcement? announcement = await ctx.Announcements
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                    .ThenInclude(x => x!.Company)
                .FirstOrDefaultAsync(x => x.Id == announcementId && x.IsActive);

            if (announcement is null)
            {
                return Result<int>.Fail("Announcement not found.");
            }

            if (!scope.IsAdmin && announcement.LocationId != scope.LocationId)
            {
                return Result<int>.Fail("You do not have access to this announcement.");
            }

            if (announcement.LocationId is not int locationId || announcement.Location is null)
            {
                return Result<int>.Fail("An announcement must be assigned to a location before it can be sent.");
            }

            if (announcement.ExpiryDate is DateTime expiry && expiry <= DateTime.UtcNow)
            {
                return Result<int>.Fail("This announcement has expired, so it can't be sent.");
            }

            // Everyone who belongs to the location, the same people its chat channel holds. The
            // sender already knows what they wrote.
            List<string> recipients = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId && x.UserId != senderUserId)
                .Select(x => x.UserId)
                .Distinct()
                .ToListAsync();

            recipients.Remove(ApplicationDbContext.SystemDeletedUserPlaceholderId);

            string? authorName = null;

            if (senderUserId is not null)
            {
                UserProfile? sender = await ctx.Users
                    .AsNoTracking()
                    .TagWithCallSite()
                    .FirstOrDefaultAsync(x => x.Id == senderUserId);

                authorName = sender is null ? null : sender.GetName().Trim() is { Length: > 0 } name ? name : sender.UserName;
            }

            _notifications.Enqueue(recipients, NotificationTopic.Announcement, new AnnouncementPayload(
                locationId,
                announcement.Location.GetDisplayName(),
                announcement.Id,
                announcement.Title,
                announcement.Body,
                authorName));

            _logger.LogInformation("User {UserId} sent announcement {AnnouncementId} to {Count} people at location {LocationId}",
                senderUserId, announcement.Id, recipients.Count, locationId);

            return Result<int>.Ok(recipients.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send announcement {AnnouncementId}", announcementId);

            return Result<int>.Fail($"Failed to send announcement: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteAnnouncementAsync(Guid announcementId, LocationScope scope)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Announcement? announcement = await ctx.Announcements.FirstOrDefaultAsync(x => x.Id == announcementId);

            if (announcement is null)
            {
                return Result<bool>.Fail("Announcement not found.");
            }

            if (!scope.IsAdmin && announcement.LocationId != scope.LocationId)
            {
                return Result<bool>.Fail("You do not have access to this announcement.");
            }

            // Soft delete, per the IsActive convention used across this codebase.
            announcement.IsActive = false;
            announcement.LastUpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete announcement {AnnouncementId}", announcementId);

            return Result<bool>.Fail($"Failed to delete announcement: {ex.Message}");
        }
    }
}
