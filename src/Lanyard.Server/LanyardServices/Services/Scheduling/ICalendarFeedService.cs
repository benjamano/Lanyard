using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Scheduling;

// HasFeed false = the person hasn't got a live feed at this company; the other fields are then null.
public record CalendarFeedStatus(bool HasFeed, string? TokenHint, DateTime? CreateDate, DateTime? LastAccessUtc);

// RawToken is only ever available here, straight after creating the feed - only its hash is kept.
public record CreatedCalendarFeed(string RawToken, CalendarFeedStatus Status);

public record CalendarFeedDocument(string Body, string ETag);

public interface ICalendarFeedService
{
    Task<Result<CalendarFeedStatus>> GetStatusAsync(string userId, int companyId);

    // Creates the person's feed, revoking any feed they already had at this company (so this is
    // also "regenerate"). Fails when the company doesn't have shift management, or the person
    // isn't a member of any of its active locations.
    Task<Result<CreatedCalendarFeed>> CreateAsync(string userId, int companyId);

    Task<Result<bool>> RevokeAsync(string userId, int companyId);

    // The .ics for a feed URL's token, for the anonymous feed endpoint. Fails with NotFoundError for
    // a token that doesn't match a live feed. A feed whose company has turned shift management off,
    // or whose person is locked out or no longer works at any of its locations, comes back as an
    // empty calendar rather than an error, so it fills again if that changes. baseUrl (the app's
    // origin) is used for the "view in Lanyard" link on each event, and left out when null.
    Task<Result<CalendarFeedDocument>> BuildFeedAsync(string rawToken, string? baseUrl);
}
