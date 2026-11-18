using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services.BookingImport;

public class BookingImportRetentionOptions
{
    public const string SectionName = "BookingImport:Retention";

    // How long import run history is kept. Zero or negative disables the purge.
    public int RetentionDays { get; set; } = 90;
}

// Purges booking import run history past its retention, across every location. Runs as the system
// tenant because it isn't acting for anyone signed in.
public class BookingImportRetentionHostedService(
    ISystemDbContextFactory factory,
    IOptions<BookingImportRetentionOptions> options,
    ILogger<BookingImportRetentionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(12);

    private readonly ISystemDbContextFactory _factory = factory;
    private readonly IOptions<BookingImportRetentionOptions> _options = options;
    private readonly ILogger<BookingImportRetentionHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("BookingImportRetentionHostedService started (retention {RetentionDays} days)", _options.Value.RetentionDays);

            using PeriodicTimer timer = new(SweepInterval);

            do
            {
                try
                {
                    await PurgeAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error purging old booking import runs");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    // Deletes runs older than the retention window. Returns the number removed.
    public async Task<int> PurgeAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        int retentionDays = _options.Value.RetentionDays;

        if (retentionDays <= 0)
        {
            return 0;
        }

        DateTime cutoff = utcNow.AddDays(-retentionDays);

        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

        // ExecuteDelete needs a relational provider; the EF InMemory test host would throw.
        if (!ctx.Database.IsRelational())
        {
            return 0;
        }

        int deleted = await ctx.BookingImportRuns
            .Where(r => r.StartedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("Purged {Count} booking import runs older than {Cutoff:u}", deleted, cutoff);
        }

        return deleted;
    }
}
