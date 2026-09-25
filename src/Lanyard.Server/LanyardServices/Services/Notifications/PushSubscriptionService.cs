using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Notifications;

public class PushSubscriptionService(
    IDbContextFactory<ApplicationDbContext> factory,
    TimeProvider timeProvider,
    ILogger<PushSubscriptionService> logger) : IPushSubscriptionService
{
    // Push service URLs are a few hundred characters; anything far longer isn't one.
    private const int MaxEndpointLength = 2000;

    // The app reports in on every load; LastSeenUtc only feeds the 90-day cleanup, so an unchanged
    // row needs touching now and then, not every time.
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<PushSubscriptionService> _logger = logger;

    public async Task<Result<bool>> SaveAsync(string userId, PushSubscriptionInput input, DeviceReport device)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(input.Endpoint) || string.IsNullOrWhiteSpace(input.P256dh) || string.IsNullOrWhiteSpace(input.Auth))
            {
                return Result<bool>.Fail("The browser didn't give a complete subscription.");
            }

            if (input.Endpoint.Length > MaxEndpointLength
                || !Uri.TryCreate(input.Endpoint, UriKind.Absolute, out Uri? endpointUri)
                || endpointUri.Scheme != Uri.UriSchemeHttps)
            {
                return Result<bool>.Fail("That isn't a push service address.");
            }

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            string label = DeviceDescriptor.Describe(device).Label;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            UserPushSubscription? existing = await ctx.PushSubscriptions
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Endpoint == input.Endpoint);

            if (existing is null)
            {
                ctx.PushSubscriptions.Add(new UserPushSubscription
                {
                    UserId = userId,
                    Endpoint = input.Endpoint,
                    P256dh = input.P256dh,
                    Auth = input.Auth,
                    DeviceLabel = label,
                    CreatedUtc = now,
                    LastSeenUtc = now
                });

                _logger.LogInformation("Saved a push subscription for {UserId} on {DeviceLabel}", userId, label);
            }
            else
            {
                bool unchanged = existing.UserId == userId
                    && existing.P256dh == input.P256dh
                    && existing.Auth == input.Auth
                    && existing.DeviceLabel == label;

                if (unchanged && now - existing.LastSeenUtc < TouchInterval)
                {
                    return Result<bool>.Ok(true);
                }

                if (existing.UserId != userId)
                {
                    // Someone else is using this browser now; their notifications must not reach
                    // the previous person, or the other way round.
                    _logger.LogInformation("Moved a push subscription on {DeviceLabel} from {OldUserId} to {UserId}", label, existing.UserId, userId);

                    existing.UserId = userId;
                    existing.CreatedUtc = now;
                    existing.LastSuccessUtc = null;
                    existing.ConsecutiveFailures = 0;
                }

                existing.P256dh = input.P256dh;
                existing.Auth = input.Auth;
                existing.DeviceLabel = label;
                existing.LastSeenUtc = now;
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (DbUpdateException ex)
        {
            // Two tabs syncing the same new subscription at once hit the unique endpoint index,
            // and the other one saved it - fine. Anything else (the account was deleted meanwhile,
            // another constraint) really failed, and the person must not be told it worked.
            if (await IsSavedForAsync(userId, input.Endpoint))
            {
                _logger.LogWarning("Push subscription save for {UserId} lost a race: {Error}", userId, ex.InnerException?.Message ?? ex.Message);

                return Result<bool>.Ok(true);
            }

            _logger.LogError(ex, "Error saving a push subscription for {UserId}", userId);

            return Result<bool>.Fail($"Couldn't save notifications for this device: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving a push subscription for {UserId}", userId);

            return Result<bool>.Fail($"Couldn't save notifications for this device: {ex.Message}");
        }
    }

    private async Task<bool> IsSavedForAsync(string userId, string endpoint)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            return await ctx.PushSubscriptions
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.Endpoint == endpoint && x.UserId == userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error re-checking a push subscription for {UserId}", userId);

            return false;
        }
    }

    public async Task<Result<List<PushDeviceView>>> GetForUserAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<PushDeviceView> devices = await ctx.PushSubscriptions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == userId)
                .OrderByDescending(x => x.LastSeenUtc)
                .Select(x => new PushDeviceView(x.Id, x.DeviceLabel, x.Endpoint, x.CreatedUtc, x.LastSeenUtc, x.LastSuccessUtc))
                .ToListAsync();

            return Result<List<PushDeviceView>>.Ok(devices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading push devices for {UserId}", userId);

            return Result<List<PushDeviceView>>.Fail($"Couldn't load your devices: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveAsync(string userId, string endpoint)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<UserPushSubscription> rows = await ctx.PushSubscriptions
                .TagWithCallSite()
                .Where(x => x.UserId == userId && x.Endpoint == endpoint)
                .ToListAsync();

            ctx.PushSubscriptions.RemoveRange(rows);
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(rows.Count > 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing a push subscription for {UserId}", userId);

            return Result<bool>.Fail($"Couldn't turn off notifications for this device: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveByIdAsync(string userId, Guid subscriptionId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            UserPushSubscription? row = await ctx.PushSubscriptions
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == subscriptionId && x.UserId == userId);

            if (row is null)
            {
                return Result<bool>.Fail("That device isn't in your list.");
            }

            ctx.PushSubscriptions.Remove(row);
            await ctx.SaveChangesAsync();

            _logger.LogInformation("{UserId} removed push device {DeviceLabel}", userId, row.DeviceLabel);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing push device {SubscriptionId} for {UserId}", subscriptionId, userId);

            return Result<bool>.Fail($"Couldn't remove that device: {ex.Message}");
        }
    }

    public async Task<Result<int>> RemoveStaleAsync(DateTime cutoffUtc)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<UserPushSubscription> stale = await ctx.PushSubscriptions
                .TagWithCallSite()
                .Where(x => x.LastSeenUtc < cutoffUtc)
                .ToListAsync();

            ctx.PushSubscriptions.RemoveRange(stale);
            await ctx.SaveChangesAsync();

            return Result<int>.Ok(stale.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing stale push subscriptions");

            return Result<int>.Fail($"Couldn't remove old devices: {ex.Message}");
        }
    }
}
