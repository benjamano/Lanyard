using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Features;

// Direct reads for services that already hold a DbContext and have no signed-in user to resolve
// a scope from (e.g. a clock-in terminal, which is anonymous and tied to a location instead).
public static class CompanyFeatureQueries
{
    public static async Task<HashSet<CompanyFeature>> GetDisabledAsync(ApplicationDbContext ctx, int companyId)
    {
        List<CompanyFeature> disabled = await ctx.CompanyFeatureSettings
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.CompanyId == companyId && !x.IsEnabled)
            .Select(x => x.Feature)
            .ToListAsync();

        return [.. disabled];
    }

    public static async Task<bool> IsEnabledForCompanyAsync(ApplicationDbContext ctx, int companyId, CompanyFeature feature, CancellationToken cancellationToken = default)
    {
        return !await ctx.CompanyFeatureSettings
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(x => x.CompanyId == companyId && x.Feature == feature && !x.IsEnabled, cancellationToken);
    }

    public static async Task<bool> IsEnabledForLocationAsync(ApplicationDbContext ctx, int locationId, CompanyFeature feature)
    {
        return !await ctx.CompanyFeatureSettings
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(x => x.Feature == feature
                && !x.IsEnabled
                && ctx.Locations.Any(l => l.Id == locationId && l.CompanyId == x.CompanyId));
    }
}
