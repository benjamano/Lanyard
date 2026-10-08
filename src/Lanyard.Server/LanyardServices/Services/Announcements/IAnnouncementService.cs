using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Announcements;

public interface IAnnouncementService
{
    // The management list - includes expired announcements, so a manager can see and tidy up what
    // has aged out. allLocations only means anything for an Admin, exactly as in ICourseService.
    Task<Result<List<Announcement>>> GetAnnouncementsAsync(LocationScope scope, bool allLocations);

    // What the dashboard widget calls: live announcements for the viewer's own location only.
    Task<Result<List<Announcement>>> GetActiveAnnouncementsAsync(LocationScope scope, int maxItems);

    Task<Result<Announcement>> SaveAnnouncementAsync(Announcement announcement, LocationScope scope);

    // Emails (and pushes, per each person's notification settings) the announcement to everyone at
    // its location except the sender. Returns how many people it was queued for.
    Task<Result<int>> SendAnnouncementAsync(Guid announcementId, LocationScope scope, string? senderUserId);

    Task<Result<bool>> DeleteAnnouncementAsync(Guid announcementId, LocationScope scope);
}
