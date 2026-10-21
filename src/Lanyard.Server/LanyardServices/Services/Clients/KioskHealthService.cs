using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.DTO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services.Clients;

public class KioskHealthService(
    IDbContextFactory<ApplicationDbContext> factory,
    IOptions<KioskHealthOptions> options,
    TimeProvider timeProvider,
    ILogger<KioskHealthService> logger) : IKioskHealthService
{
    // Anyone holding the shared kiosk secret can call the hub as any client, so a report is
    // ignored if it comes in faster than this rather than turning into a write per call.
    internal static readonly TimeSpan MinReportSpacing = TimeSpan.FromSeconds(10);

    private const int MaxProjectionCount = 64;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly KioskHealthOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<KioskHealthService> _logger = logger;

    public async Task<Result<bool>> RecordReportAsync(Guid clientId, KioskHealthReportDTO report)
    {
        try
        {
            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ClientHealthSnapshot? snapshot = await ctx.ClientHealthSnapshots
                .FirstOrDefaultAsync(x => x.ClientId == clientId);

            if (snapshot == null)
            {
                if (!await ctx.Clients.AnyAsync(x => x.Id == clientId))
                {
                    return Result<bool>.Fail("Client not found");
                }

                snapshot = new ClientHealthSnapshot { ClientId = clientId, FirstHeartbeatUtc = now };
                ctx.ClientHealthSnapshots.Add(snapshot);
            }
            else if (now - snapshot.LastHeartbeatUtc < MinReportSpacing)
            {
                return Result<bool>.Ok(false);
            }

            Apply(snapshot, report, now);

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record a health report for client {ClientId}", clientId);
            return Result<bool>.Fail($"Failed to record the health report: {ex.Message}");
        }
    }

    public async Task<Result<Dictionary<Guid, KioskHealthView>>> GetHealthAsync()
    {
        try
        {
            HashSet<string> connectedIds = GetConnectedConnectionIds().ToHashSet();

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<KioskHealthClient> clients = (await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Select(x => new { x.Id, x.MostRecentConnectionId, x.LastDisconnectDate })
                .ToListAsync())
                .Select(x => new KioskHealthClient(
                    x.Id,
                    x.MostRecentConnectionId != null && connectedIds.Contains(x.MostRecentConnectionId),
                    x.LastDisconnectDate))
                .ToList();

            return Result<Dictionary<Guid, KioskHealthView>>.Ok(await EvaluateAsync(ctx, clients));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load kiosk health");
            return Result<Dictionary<Guid, KioskHealthView>>.Fail($"Failed to load kiosk health: {ex.Message}");
        }
    }

    public async Task<Result<Dictionary<Guid, KioskHealthView>>> GetHealthAsync(IReadOnlyCollection<KioskHealthClient> clients)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            return Result<Dictionary<Guid, KioskHealthView>>.Ok(await EvaluateAsync(ctx, clients));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load kiosk health");
            return Result<Dictionary<Guid, KioskHealthView>>.Fail($"Failed to load kiosk health: {ex.Message}");
        }
    }

    public async Task<Result<KioskHealthView?>> GetClientHealthAsync(Guid clientId)
    {
        try
        {
            HashSet<string> connectedIds = GetConnectedConnectionIds().ToHashSet();

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            var client = await ctx.Clients
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == clientId)
                .Select(x => new { x.Id, x.MostRecentConnectionId, x.LastDisconnectDate })
                .FirstOrDefaultAsync();

            if (client == null)
            {
                return Result<KioskHealthView?>.Ok(null);
            }

            ClientHealthSnapshot? snapshot = await ctx.ClientHealthSnapshots
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.ClientId == clientId);

            // "A newer version is out there" compares against the whole company's kiosks, so the
            // other snapshots' versions are still needed, just not their rows.
            List<string?> versions = await ctx.ClientHealthSnapshots
                .AsNoTracking()
                .TagWithCallSite()
                .Select(x => x.ClientVersion)
                .ToListAsync();

            KioskHealthView view = KioskHealthEvaluator.Evaluate(
                snapshot,
                client.MostRecentConnectionId != null && connectedIds.Contains(client.MostRecentConnectionId),
                client.LastDisconnectDate,
                _timeProvider.GetUtcNow().UtcDateTime,
                _options,
                KioskHealthEvaluator.Newest(versions));

            return Result<KioskHealthView?>.Ok(view);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load health for client {ClientId}", clientId);
            return Result<KioskHealthView?>.Fail($"Failed to load kiosk health: {ex.Message}");
        }
    }

    private async Task<Dictionary<Guid, KioskHealthView>> EvaluateAsync(ApplicationDbContext ctx, IReadOnlyCollection<KioskHealthClient> clients)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        Dictionary<Guid, ClientHealthSnapshot> snapshots = await ctx.ClientHealthSnapshots
            .AsNoTracking()
            .TagWithCallSite()
            .ToDictionaryAsync(x => x.ClientId);

        string? newestVersion = KioskHealthEvaluator.Newest(snapshots.Values.Select(x => x.ClientVersion));

        return clients.ToDictionary(
            x => x.Id,
            x => KioskHealthEvaluator.Evaluate(
                snapshots.GetValueOrDefault(x.Id),
                x.IsConnected,
                x.LastDisconnectDate,
                now,
                _options,
                newestVersion));
    }

    // Same seam as ClientService.GetConnectedConnectionIds: the hub's connection set is
    // process-static, so tests override this instead.
    protected internal virtual IReadOnlyCollection<string> GetConnectedConnectionIds()
    {
        return SignalRControlHub.ConnectedIds;
    }

    private static void Apply(ClientHealthSnapshot snapshot, KioskHealthReportDTO report, DateTime now)
    {
        snapshot.LastHeartbeatUtc = now;

        // Same rule as the version a kiosk reports on connect: only a short SemVer is kept.
        snapshot.ClientVersion = ClientVersionFormat.TryNormalize(report.ClientVersion, out string version) ? version : null;

        snapshot.ProcessStartedUtc = DateTime.SpecifyKind(report.ProcessStartedUtc, DateTimeKind.Utc);
        snapshot.MachineUptimeSeconds = Math.Max(0, report.MachineUptimeSeconds);

        snapshot.CpuPercent = ClampPercent(report.CpuPercent);
        snapshot.MemoryPercent = ClampPercent(report.MemoryPercent);

        snapshot.SystemDriveFreeBytes = report.SystemDriveFreeBytes is >= 0 ? report.SystemDriveFreeBytes : null;
        snapshot.SystemDriveTotalBytes = report.SystemDriveTotalBytes is > 0 ? report.SystemDriveTotalBytes : null;

        snapshot.DmxConfigured = report.DmxConfigured;
        snapshot.DmxOpen = report.DmxOpen;
        snapshot.PreferredAudioDevicePresent = report.PreferredAudioDevicePresent;
        snapshot.PacketCaptureConfigured = report.PacketCaptureConfigured;
        snapshot.PacketCaptureRunning = report.PacketCaptureRunning;
        snapshot.ProjectionRunningCount = Math.Clamp(report.ProjectionRunningCount, 0, MaxProjectionCount);

        snapshot.OfflineFallbackReady = report.OfflineFallbackReady;
        snapshot.OfflineVideosNotCached = report.OfflineVideosNotCached is >= 0 ? report.OfflineVideosNotCached : null;
        snapshot.OfflineCacheBytes = report.OfflineCacheBytes is >= 0 ? report.OfflineCacheBytes : null;
        snapshot.OfflineCacheSyncedUtc = AsUtc(report.OfflineCacheSyncedUtc);
        snapshot.LastOfflineStartedUtc = AsUtc(report.LastOfflineStartedUtc);
        snapshot.LastOfflineEndedUtc = AsUtc(report.LastOfflineEndedUtc);
    }

    private static DateTime? AsUtc(DateTime? value)
    {
        return value is DateTime v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
    }

    private static double? ClampPercent(double? value)
    {
        if (value is not double v || double.IsNaN(v) || double.IsInfinity(v))
        {
            return null;
        }

        return Math.Clamp(v, 0, 100);
    }
}
