using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lanyard.Application.Services.Demo;

public interface IDemoResetService
{
    // Wipes the demo company and seeds it afresh (creating it the first time).
    Task<Result<bool>> ResetAsync(CancellationToken ct = default);

    // When the demo was last reset (UTC), or null if it's never been seeded.
    Task<DateTime?> GetLastResetUtcAsync(CancellationToken ct = default);
}

public sealed class DemoResetService(
    DbContextOptions<ApplicationDbContext> dbOptions,
    IServiceScopeFactory scopeFactory,
    IOptions<DemoOptions> demoOptions,
    IDemoDirectory demoDirectory,
    TimeProvider timeProvider,
    ILogger<DemoResetService> logger) : IDemoResetService
{
    internal const string LastResetKey = "Demo.LastResetUtc";

    // One reset at a time: the nightly run and an admin's "Reset now" must not interleave.
    private static readonly SemaphoreSlim ResetLock = new(1, 1);

    public async Task<Result<bool>> ResetAsync(CancellationToken ct = default)
    {
        if (!demoOptions.Value.Enabled)
        {
            return Result<bool>.Fail("The demo isn't enabled on this server.");
        }

        await ResetLock.WaitAsync(ct);

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
            DemoSeeder seeder = new(dbOptions, userManager, timeProvider);

            int companyId = await seeder.EnsureCompanyAsync(ct);

            logger.LogInformation("Resetting the demo company {CompanyId}", companyId);

            // Not one transaction: the account steps go through Identity's own context. Every
            // step is safe to repeat, so a reset that fails part-way is fixed by the next one.
            await DemoDataWiper.WipeAsync(dbOptions, companyId, ct);
            await seeder.ResetPeopleAndPlacesAsync(companyId, ct);
            await seeder.SeedDataAsync(companyId, ct);

            await using (ApplicationDbContext ctx = new(dbOptions))
            {
                ctx.AppSettings.Add(new AppSetting
                {
                    Id = Guid.NewGuid(),
                    CompanyId = companyId,
                    Key = LastResetKey,
                    Value = timeProvider.GetUtcNow().UtcDateTime.ToString("O"),
                    CreateDate = timeProvider.GetUtcNow().UtcDateTime,
                });
                await ctx.SaveChangesAsync(ct);
            }

            demoDirectory.Invalidate();

            logger.LogInformation("Demo company {CompanyId} reset", companyId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resetting the demo company failed");
            return Result<bool>.Fail($"Resetting the demo failed: {ex.Message}");
        }
        finally
        {
            ResetLock.Release();
        }
    }

    public async Task<DateTime?> GetLastResetUtcAsync(CancellationToken ct = default)
    {
        await using ApplicationDbContext ctx = new(dbOptions);

        string? value = await ctx.AppSettings
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.Key == LastResetKey && ctx.Companies.Any(c => c.Id == x.CompanyId && c.IsDemo))
            .OrderByDescending(x => x.CreateDate)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(ct);

        return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime at) ? at : null;
    }
}
