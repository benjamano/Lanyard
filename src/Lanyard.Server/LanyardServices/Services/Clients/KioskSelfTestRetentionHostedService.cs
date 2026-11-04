using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services;

public class KioskSelfTestOptions
{
    public const string SectionName = "Kiosks:SelfTest";

    /// <summary>How long self-test results are kept. Zero or negative disables the purge.</summary>
    public int RetentionDays { get; set; } = 90;
}

/// <summary>
/// Purges kiosk self-test results past their retention, across every company. Runs as the system
/// tenant (<see cref="ISystemDbContextFactory"/>) because it isn't acting for anyone signed in.
/// </summary>
public class KioskSelfTestRetentionHostedService(
    ISystemDbContextFactory factory,
    IOptions<KioskSelfTestOptions> options,
    ILogger<KioskSelfTestRetentionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(12);

    private readonly ISystemDbContextFactory _factory = factory;
    private readonly IOptions<KioskSelfTestOptions> _options = options;
    private readonly ILogger<KioskSelfTestRetentionHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("KioskSelfTestRetentionHostedService started (retention {RetentionDays} days)", _options.Value.RetentionDays);

            using PeriodicTimer timer = new(SweepInterval);

            do
            {
                try
                {
                    await PurgeAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error purging old kiosk self-test results");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    /// <summary>Deletes runs (and their checks) older than the retention window. Returns the number of runs removed.</summary>
    public async Task<int> PurgeAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        int retentionDays = _options.Value.RetentionDays;

        if (retentionDays <= 0)
        {
            return 0;
        }

        DateTime cutoff = utcNow.AddDays(-retentionDays);

        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

        // ExecuteDelete needs a relational provider; the EF InMemory database the integration
        // test host runs on would throw on every sweep.
        if (!ctx.Database.IsRelational())
        {
            return 0;
        }

        // Children first: ExecuteDelete bypasses EF's cascade handling.
        await ctx.ClientSelfTestChecks
            .Where(c => c.Run!.RequestedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        int deleted = await ctx.ClientSelfTestRuns
            .Where(r => r.RequestedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("Purged {Count} kiosk self-test results older than {Cutoff:u}", deleted, cutoff);
        }

        return deleted;
    }
}
