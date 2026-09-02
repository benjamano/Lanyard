using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Kitchen;

/// <summary>
/// Clears the free-text note off orders once they are 30 days old.
///
/// The note field asks customers for "allergies, no onions", so in practice it collects health
/// information from members of the public. Keeping that indefinitely has no operational purpose
/// once the food has been served and any dispute window has closed, and storage limitation says
/// not to.
///
/// Only the note is cleared. The order itself, its lines, prices and allergen declarations are
/// financial and food-safety records and stay: a refund enquiry or an EHO visit months later
/// still needs to see what was sold and what was declared. Those declarations come from the menu
/// rather than from anything the customer typed, so clearing the note does not weaken them.
/// </summary>
public class OrderNoteRetentionHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<OrderNoteRetentionHostedService> logger) : BackgroundService
{
    /// <summary>
    /// Daily. The retention period is measured in days, so sweeping more often would only add
    /// load without clearing anything sooner.
    /// </summary>
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromDays(1);

    private static readonly TimeSpan _retainNotesFor = TimeSpan.FromDays(30);

    /// <summary>
    /// Bounded per pass. The first run after this ships could face every note ever written, and
    /// loading all of them into one change tracker is how a background job takes the app down
    /// with it. Whatever is left over is picked up by the next pass.
    /// </summary>
    private const int BatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<OrderNoteRetentionHostedService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation(
                "OrderNoteRetentionHostedService started; customer notes are cleared after {Days} days",
                _retainNotesFor.TotalDays);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSweepAsync(stoppingToken);
                await Task.Delay(_sweepInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OrderNoteRetentionHostedService stopped unexpectedly");
        }
    }

    private async Task RunSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IDbContextFactory<ApplicationDbContext> factory =
                scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

            DateTime cutoff = DateTime.UtcNow - _retainNotesFor;
            int cleared = 0;

            // Loops until a short batch comes back, so a large backlog is worked through in one
            // pass rather than 500 rows a day.
            while (!stoppingToken.IsCancellationRequested)
            {
                await using ApplicationDbContext ctx = await factory.CreateDbContextAsync(stoppingToken);

                List<KitchenOrder> batch = await ctx.KitchenOrders
                    .Where(o => o.CustomerNote != null && o.CreateDate < cutoff)
                    .OrderBy(o => o.Id)
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken);

                if (batch.Count == 0)
                {
                    break;
                }

                foreach (KitchenOrder order in batch)
                {
                    order.CustomerNote = null;
                }

                // UpdateDate is deliberately left alone. It means "when did this order last
                // change", and housekeeping is not a change to the order - moving it would make
                // every old order look freshly touched on the day this first ran.
                await ctx.SaveChangesAsync(stoppingToken);

                cleared += batch.Count;

                if (batch.Count < BatchSize)
                {
                    break;
                }
            }

            if (cleared > 0)
            {
                // Counted, never logged in full: writing the notes out to keep a record of what
                // was deleted would defeat the entire point of deleting them.
                _logger.LogInformation("Cleared the customer note on {Count} order(s) older than {Days} days",
                    cleared, _retainNotesFor.TotalDays);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Swallowed so one bad pass does not stop the loop; the next one runs tomorrow.
            _logger.LogError(ex, "Customer-note retention sweep failed");
        }
    }
}
