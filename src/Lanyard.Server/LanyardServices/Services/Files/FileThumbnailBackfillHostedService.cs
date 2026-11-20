using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

/// <summary>
/// Makes Files' grid thumbnails for images uploaded before thumbnails existed. Works through them a
/// few at a time in the background, then stops: new uploads get theirs as they're uploaded. Each
/// file is tried once (ThumbnailPath becomes empty when there can't be one), so a broken image
/// isn't retried on every restart.
/// </summary>
public sealed class FileThumbnailBackfillHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<FileThumbnailBackfillHostedService> logger) : BackgroundService
{
    private const int BatchSize = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Out of the way of startup.
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

            int made = 0;
            int skipped = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                ISystemDbContextFactory factory = scope.ServiceProvider.GetRequiredService<ISystemDbContextFactory>();
                IFileService files = scope.ServiceProvider.GetRequiredService<IFileService>();

                List<Guid> batch;

                await using (ApplicationDbContext db = await factory.CreateDbContextAsync(stoppingToken))
                {
                    batch = await db.FileMetadata.AsNoTracking().TagWithCallSite()
                        .Where(x => x.ThumbnailPath == null && !x.IsRestricted && x.ContentType != null && x.ContentType.StartsWith("image/"))
                        .OrderBy(x => x.UploadedAt)
                        .Select(x => x.Id)
                        .Take(BatchSize)
                        .ToListAsync(stoppingToken);
                }

                if (batch.Count == 0)
                    break;

                foreach (Guid id in batch)
                {
                    Result<bool> result = await files.CreateMissingThumbnailAsync(id, stoppingToken);

                    if (result.Data)
                        made++;
                    else
                        skipped++;

                    // A failure that couldn't even mark the row would loop forever; stop instead.
                    if (!result.IsSuccess)
                    {
                        logger.LogWarning("Stopped making thumbnails after {FileId} failed: {Error}", id, result.Error);
                        return;
                    }
                }

                // Gentle on the bucket and the CPU.
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }

            if (made + skipped > 0)
                logger.LogInformation("Made {Made} file thumbnails for older images ({Skipped} couldn't have one)", made, skipped);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "File thumbnail backfill stopped with an error");
        }
    }
}
