using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Notifications;

public class AppInstallationService(
    IDbContextFactory<ApplicationDbContext> factory,
    TimeProvider timeProvider,
    ILogger<AppInstallationService> logger) : IAppInstallationService
{
    // Every page load of the installed app reports in; the row only needs touching now and then.
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<AppInstallationService> _logger = logger;

    public async Task<Result<bool>> RecordAsync(string userId, string deviceId, DeviceReport device)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 64)
            {
                return Result<bool>.Fail("Missing device id.");
            }

            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            DeviceDescription description = DeviceDescriptor.Describe(device with { IsStandalone = true });

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            AppInstallation? existing = await ctx.AppInstallations
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.UserId == userId && x.DeviceId == deviceId);

            if (existing is null)
            {
                ctx.AppInstallations.Add(new AppInstallation
                {
                    UserId = userId,
                    DeviceId = deviceId,
                    DeviceLabel = description.Label,
                    Platform = description.Platform,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                });

                _logger.LogInformation("{UserId} is using the installed app on {DeviceLabel}", userId, description.Label);
            }
            else if (now - existing.LastSeenUtc >= TouchInterval)
            {
                existing.LastSeenUtc = now;
                existing.DeviceLabel = description.Label;
                existing.Platform = description.Platform;
            }
            else
            {
                return Result<bool>.Ok(true);
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning("App installation record for {UserId} lost a race: {Error}", userId, ex.InnerException?.Message ?? ex.Message);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording an app installation for {UserId}", userId);

            return Result<bool>.Fail($"Couldn't record the app install: {ex.Message}");
        }
    }

    public async Task<Result<DeviceReachSummary>> GetSummaryAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            int installed = await ctx.AppInstallations
                .AsNoTracking()
                .TagWithCallSite()
                .CountAsync(x => x.UserId == userId);

            int push = await ctx.PushSubscriptions
                .AsNoTracking()
                .TagWithCallSite()
                .CountAsync(x => x.UserId == userId);

            return Result<DeviceReachSummary>.Ok(new DeviceReachSummary(installed, push));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading device summary for {UserId}", userId);

            return Result<DeviceReachSummary>.Fail($"Couldn't load devices: {ex.Message}");
        }
    }

    public async Task<Result<int>> RemoveStaleAsync(DateTime cutoffUtc)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<AppInstallation> stale = await ctx.AppInstallations
                .TagWithCallSite()
                .Where(x => x.LastSeenUtc < cutoffUtc)
                .ToListAsync();

            ctx.AppInstallations.RemoveRange(stale);
            await ctx.SaveChangesAsync();

            return Result<int>.Ok(stale.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing stale app installations");

            return Result<int>.Fail($"Couldn't remove old installs: {ex.Message}");
        }
    }
}
