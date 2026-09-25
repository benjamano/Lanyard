#nullable enable

using Lanyard.Infrastructure.DataAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services;

public class AutomationExecutionLogOptions
{
    public const string SectionName = "Automation:ExecutionLog";

    /// <summary>How long execution-log rows are kept. Zero or negative disables the purge.</summary>
    public int RetentionDays { get; set; } = 90;
}

/// <summary>
/// Keeps the automation execution log bounded. Every rule fire (including idle-rule retries
/// once per threshold window and scheduled retries several times a day) appends a row plus one
/// per action, and nothing else ever deletes them - so the table, and the newest-first reads
/// against it, grew without limit.
/// </summary>
public class AutomationExecutionRetentionHostedService(
    IDbContextFactory<ApplicationDbContext> factory,
    IOptions<AutomationExecutionLogOptions> options,
    ILogger<AutomationExecutionRetentionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(12);

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IOptions<AutomationExecutionLogOptions> _options = options;
    private readonly ILogger<AutomationExecutionRetentionHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("AutomationExecutionRetentionHostedService started (retention {RetentionDays} days)", _options.Value.RetentionDays);

            using PeriodicTimer timer = new(SweepInterval);

            do
            {
                try
                {
                    await PurgeAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error purging old automation executions");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    /// <summary>Deletes executions (and their action rows) older than the retention window. Returns the number of executions removed.</summary>
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

        // Set-based deletes: nothing is loaded into memory. Children go first because
        // ExecuteDelete bypasses EF's cascade handling (the database FK cascades too, but this
        // keeps the behaviour identical on every provider).
        await ctx.AutomationRuleActionExecutions
            .Where(a => a.AutomationRuleExecution!.ExecutedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        int deleted = await ctx.AutomationRuleExecutions
            .Where(e => e.ExecutedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("Purged {Count} automation executions older than {Cutoff:u}", deleted, cutoff);
        }

        return deleted;
    }
}
