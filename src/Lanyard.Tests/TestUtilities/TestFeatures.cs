using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Tests.TestUtilities;

// Features are off at a location until a switch turns them on, so test data that relies on a
// module has to switch it on first.
public static class TestFeatures
{
    // Every feature on at every location saved so far.
    public static async Task SwitchOnEverywhereAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);
        await SwitchOnEverywhereAsync(ctx);
    }

    public static async Task SwitchOnEverywhereAsync(ApplicationDbContext ctx)
    {
        List<int> locationIds = await ctx.Locations.IgnoreQueryFilters().Select(x => x.Id).ToListAsync();

        HashSet<(int, CompanyFeature)> existing = (await ctx.LocationFeatureSettings
            .Select(x => new { x.LocationId, x.Feature })
            .ToListAsync())
            .Select(x => (x.LocationId, x.Feature))
            .ToHashSet();

        foreach (int locationId in locationIds)
        {
            foreach (CompanyFeature feature in System.Enum.GetValues<CompanyFeature>())
            {
                if (!existing.Contains((locationId, feature)))
                {
                    ctx.LocationFeatureSettings.Add(new LocationFeatureSetting { LocationId = locationId, Feature = feature, IsEnabled = true });
                }
            }
        }

        await ctx.SaveChangesAsync();
    }

    // Switches one feature off at every location of the company (the per-location version of
    // what used to be a company-wide switch). Doesn't save.
    public static async Task SwitchOffForCompanyAsync(ApplicationDbContext ctx, int companyId, CompanyFeature feature)
    {
        List<int> locationIds = await ctx.Locations.IgnoreQueryFilters().Where(x => x.CompanyId == companyId).Select(x => x.Id).ToListAsync();

        List<LocationFeatureSetting> rows = await ctx.LocationFeatureSettings
            .Where(x => locationIds.Contains(x.LocationId) && x.Feature == feature)
            .ToListAsync();

        ctx.LocationFeatureSettings.RemoveRange(rows);
    }
}
