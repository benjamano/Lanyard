using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services.OperationsAlerts;

public class OperationsAlertOptions
{
    public const string SectionName = "OperationsAlerts";

    /// <summary>
    /// How long a resolved alert is kept. Never less than the History tab's
    /// <see cref="OperationsAlertService.HistoryDays"/>; zero or negative disables the purge.
    /// Unresolved alerts are never purged, however old.
    /// </summary>
    public int RetentionDays { get; set; } = OperationsAlertService.HistoryDays;
}

/// <summary>
/// Deletes resolved operations alerts past their retention, across every company. Alerts can name a
/// staff member (missed clock-in, overdue training), so they aren't kept forever. Runs as the
/// system tenant because it isn't acting for anyone signed in.
/// </summary>
public class OperationsAlertRetentionHostedService(
    ISystemDbContextFactory factory,
    IOptions<OperationsAlertOptions> options,
    TimeProvider timeProvider,
    ILogger<OperationsAlertRetentionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(12);

    private readonly ISystemDbContextFactory _factory = factory;
    private readonly IOptions<OperationsAlertOptions> _options = options;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<OperationsAlertRetentionHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("OperationsAlertRetentionHostedService started (retention {RetentionDays} days)", _options.Value.RetentionDays);

            using PeriodicTimer timer = new(SweepInterval, _timeProvider);

            do
            {
                try
                {
                    await PurgeAsync(_timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Unhandled error purging old operations alerts");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    /// <summary>Deletes resolved alerts older than the retention window. Returns how many went.</summary>
    public async Task<int> PurgeAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        int retentionDays = _options.Value.RetentionDays;

        if (retentionDays <= 0)
        {
            return 0;
        }

        DateTime cutoff = utcNow.AddDays(-Math.Max(retentionDays, OperationsAlertService.HistoryDays));

        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

        int deleted;

        if (ctx.Database.IsRelational())
        {
            deleted = await ctx.OperationsAlerts
                .Where(x => x.Status == OperationsAlertStatus.Resolved && x.ResolvedAtUtc < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            // The EF InMemory provider (tests, the integration host) has no ExecuteDelete.
            List<Infrastructure.Models.OperationsAlert> old = await ctx.OperationsAlerts
                .Where(x => x.Status == OperationsAlertStatus.Resolved && x.ResolvedAtUtc < cutoff)
                .ToListAsync(cancellationToken);

            ctx.OperationsAlerts.RemoveRange(old);
            await ctx.SaveChangesAsync(cancellationToken);
            deleted = old.Count;
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Purged {Count} resolved operations alerts older than {Cutoff:u}", deleted, cutoff);
        }

        return deleted;
    }
}
