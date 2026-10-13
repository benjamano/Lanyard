using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Lanyard.Application.Services.Features;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

// Private iCal feeds of a person's published shifts (see CalendarFeedToken).
//
// The feed endpoint is anonymous, and with nobody signed in the tenant filter is off (system
// scope) - so nothing here relies on it. Every feed query is pinned to the token's own UserId and
// CompanyId, and to the active locations of that company the person still belongs to. Those
// queries also ignore the tenant filter outright: the same URL opened in a browser that happens to
// be signed in to a different company must give the same answer, not an empty calendar.
//
// What a feed shows is the rota as the person was last told it: the Published* snapshot of each
// shift, including a published shift a manager has removed but not yet published the removal of.
// Drafts and unpublished edits never appear.
public class CalendarFeedService(
    IDbContextFactory<ApplicationDbContext> factory,
    TimeProvider timeProvider,
    ILogger<CalendarFeedService> logger) : ICalendarFeedService
{
    public const string NotFoundError = "Calendar feed not found.";

    // Past shifts stay visible for a month (for "when did I work?"); a published rota rarely runs
    // further ahead than this.
    private static readonly TimeSpan PastWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan FutureWindow = TimeSpan.FromDays(180);

    private static readonly TimeSpan LastAccessThrottle = TimeSpan.FromHours(1);

    private const int TokenHintLength = 4;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<CalendarFeedService> _logger = logger;

    // A plain SHA-256 is right here, as for clock-in terminals: the token is 256 random bits, so
    // there's nothing to brute-force, and the hash has to be searchable to find the feed. Looking
    // it up by equality on the hash also means the comparison can't leak the token through timing.
    public static string HashToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public async Task<Result<CalendarFeedStatus>> GetStatusAsync(string userId, int companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CalendarFeedToken? feed = await ctx.CalendarFeedTokens
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.UserId == userId && x.CompanyId == companyId && x.IsActive);

            return Result<CalendarFeedStatus>.Ok(ToStatus(feed));
        }
        catch (Exception ex)
        {
            return Result<CalendarFeedStatus>.Fail($"Failed to load your calendar feed: {ex.Message}");
        }
    }

    public async Task<Result<CreatedCalendarFeed>> CreateAsync(string userId, int companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (!await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, companyId, CompanyFeature.StaffScheduling))
            {
                return Result<CreatedCalendarFeed>.Fail(ClockInTerminalService.FeatureOffError);
            }

            bool isMember = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.UserId == userId && x.Location!.CompanyId == companyId && x.Location.IsActive);

            if (!isMember)
            {
                return Result<CreatedCalendarFeed>.Fail("You don't work at any of this company's locations.");
            }

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

            // Revoked and saved before the new feed is added: at most one active feed per person
            // per company is a unique index, and the old one has to be gone for the insert.
            int revoked = await RevokeActiveAsync(ctx, userId, companyId, now);

            string rawToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

            CalendarFeedToken feed = new()
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                UserId = userId,
                TokenHash = HashToken(rawToken),
                TokenHint = rawToken[^TokenHintLength..],
                CreateDate = now,
                IsActive = true
            };

            ctx.CalendarFeedTokens.Add(feed);
            await ctx.SaveChangesAsync();

            _logger.LogInformation("User {UserId} {Action} calendar feed {FeedId} for company {CompanyId}",
                userId, revoked > 0 ? "regenerated" : "created", feed.Id, companyId);

            return Result<CreatedCalendarFeed>.Ok(new CreatedCalendarFeed(rawToken, ToStatus(feed)));
        }
        catch (Exception ex)
        {
            return Result<CreatedCalendarFeed>.Fail($"Failed to create your calendar feed: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RevokeAsync(string userId, int companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            int revoked = await RevokeActiveAsync(ctx, userId, companyId, _timeProvider.GetUtcNow().UtcDateTime);

            if (revoked == 0)
            {
                return Result<bool>.Fail("You don't have a calendar feed to turn off.");
            }

            _logger.LogInformation("User {UserId} revoked their calendar feed for company {CompanyId}", userId, companyId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to turn off your calendar feed: {ex.Message}");
        }
    }

    public async Task<Result<CalendarFeedDocument>> BuildFeedAsync(string rawToken, string? baseUrl)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rawToken) || rawToken.Length > 100)
            {
                return Result<CalendarFeedDocument>.Fail(NotFoundError);
            }

            string hash = HashToken(rawToken);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CalendarFeedToken? feed = await ctx.CalendarFeedTokens
                .IgnoreQueryFilters()
                .TagWithCallSite()
                .Include(x => x.Company)
                .Include(x => x.User)
                .FirstOrDefaultAsync(x => x.TokenHash == hash && x.IsActive);

            if (feed is null)
            {
                return Result<CalendarFeedDocument>.Fail(NotFoundError);
            }

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

            if (feed.LastAccessUtc is null || now - feed.LastAccessUtc >= LastAccessThrottle)
            {
                feed.LastAccessUtc = now;
                await ctx.SaveChangesAsync();
            }

            string calendarName = $"Shifts – {feed.Company?.Name ?? "Lanyard"}";

            List<IcsEvent> events = await GetEventsAsync(ctx, feed, now, baseUrl);

            string body = IcsCalendarWriter.Write(calendarName, events);
            string etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..32]}\"";

            return Result<CalendarFeedDocument>.Ok(new CalendarFeedDocument(body, etag));
        }
        catch (Exception ex)
        {
            return Result<CalendarFeedDocument>.Fail($"Failed to build the calendar feed: {ex.Message}");
        }
    }

    private static async Task<List<IcsEvent>> GetEventsAsync(ApplicationDbContext ctx, CalendarFeedToken feed, DateTime now, string? baseUrl)
    {
        if (!await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, feed.CompanyId, CompanyFeature.StaffScheduling))
        {
            return [];
        }

        // Locking someone out stops their feed too - the URL works without signing in.
        if (feed.User is null || feed.User.LockoutEnd > now)
        {
            return [];
        }

        string userId = feed.UserId;
        int companyId = feed.CompanyId;

        List<int> locationIds = await ctx.UserLocationMemberships
            .AsNoTracking()
            .IgnoreQueryFilters()
            .TagWithCallSite()
            .Where(x => x.UserId == userId && x.Location!.CompanyId == companyId && x.Location.IsActive)
            .Select(x => x.LocationId)
            .ToListAsync();

        if (locationIds.Count == 0)
        {
            return [];
        }

        DateTime fromUtc = now - PastWindow;
        DateTime toUtc = now + FutureWindow;

        List<Shift> shifts = await ctx.Shifts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .TagWithCallSite()
            .Include(x => x.Location)
            .Where(x => x.UserId == userId
                && locationIds.Contains(x.LocationId)
                && x.PublishedDateUtc != null
                && (x.IsActive || x.RemovalPending)
                && (x.PublishedEndUtc ?? x.EndUtc) > fromUtc
                && (x.PublishedStartUtc ?? x.StartUtc) < toUtc)
            .OrderBy(x => x.PublishedStartUtc ?? x.StartUtc)
            .ToListAsync();

        List<Guid> positionIds = shifts
            .Select(x => x.PublishedEndUtc is null ? x.StaffPositionId : x.PublishedStaffPositionId)
            .OfType<Guid>()
            .Distinct()
            .ToList();

        Dictionary<Guid, string> positionNames = await ctx.StaffPositions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .TagWithCallSite()
            .Where(x => positionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name);

        string? shiftsUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/rota";

        return shifts.Select(x => ToEvent(x, positionNames, shiftsUrl)).ToList();
    }

    // Event text is deliberately sparse - it ends up on Google's, Apple's or Microsoft's servers.
    // Position and location only: no manager notes, no colleagues.
    public static IcsEvent ToEvent(Shift shift, IReadOnlyDictionary<Guid, string> positionNames, string? shiftsUrl)
    {
        // Rows published before the snapshot columns existed were back-filled by the migration;
        // the fallbacks only matter for a row that somehow missed it.
        bool hasSnapshot = shift.PublishedEndUtc is not null;

        DateTime startUtc = hasSnapshot ? shift.PublishedStartUtc ?? shift.StartUtc : shift.StartUtc;
        DateTime endUtc = hasSnapshot ? shift.PublishedEndUtc!.Value : shift.EndUtc;
        Guid? positionId = hasSnapshot ? shift.PublishedStaffPositionId : shift.StaffPositionId;
        int breakMinutes = hasSnapshot ? shift.PublishedBreakMinutes ?? 0 : shift.BreakMinutes;

        string? positionName = positionId is Guid id && positionNames.TryGetValue(id, out string? name) ? name : null;
        string summary = positionName is null ? "Shift" : $"{positionName} shift";

        decimal paidHours = Math.Max(0m, (decimal)(endUtc - startUtc).TotalMinutes - breakMinutes) / 60m;

        StringBuilder description = new();
        description.Append(RotaFormat.TimeRange(startUtc, endUtc));

        if (breakMinutes > 0)
        {
            description.Append($" ({breakMinutes} min break, {RotaFormat.Hours(paidHours)} paid)");
        }

        if (shiftsUrl is not null)
        {
            description.Append($"\nSee your shifts in Lanyard: {shiftsUrl}");
        }

        return new IcsEvent(
            Uid: $"{shift.Id:N}@lanyard-shift",
            StartUtc: startUtc,
            EndUtc: endUtc,
            LastModifiedUtc: shift.PublishedDateUtc ?? shift.CreateDate,
            Summary: summary,
            Location: shift.Location?.Name,
            Description: description.ToString(),
            Url: shiftsUrl);
    }

    private static async Task<int> RevokeActiveAsync(ApplicationDbContext ctx, string userId, int companyId, DateTime now)
    {
        List<CalendarFeedToken> active = await ctx.CalendarFeedTokens
            .TagWithCallSite()
            .Where(x => x.UserId == userId && x.CompanyId == companyId && x.IsActive)
            .ToListAsync();

        foreach (CalendarFeedToken feed in active)
        {
            feed.IsActive = false;
            feed.RevokedDateUtc = now;
            feed.RevokedByUserId = userId;
        }

        if (active.Count > 0)
        {
            await ctx.SaveChangesAsync();
        }

        return active.Count;
    }

    private static CalendarFeedStatus ToStatus(CalendarFeedToken? feed) => feed is null
        ? new CalendarFeedStatus(false, null, null, null)
        : new CalendarFeedStatus(true, feed.TokenHint, feed.CreateDate, feed.LastAccessUtc);
}
