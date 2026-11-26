using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Features;

// Direct reads for services that already hold a DbContext and have no signed-in user to resolve
// a scope from (e.g. a clock-in terminal, which is anonymous and tied to a location instead).
//
// Features are switched on per location, and only an enabled row counts. Where a location is
// known, ask about that location. A "company" check is for work that spans the whole company
// (calendar feeds, onboarding, company-wide alerts): it passes when any active location of the
// company has the feature.
public static class CompanyFeatureQueries
{
    // The enabled switches at the company's active locations.
    public static IQueryable<LocationFeatureSetting> EnabledForCompany(ApplicationDbContext ctx, int companyId) =>
        ctx.LocationFeatureSettings.Where(x => x.IsEnabled
            && ctx.Locations.Any(l => l.Id == x.LocationId && l.CompanyId == companyId && l.IsActive));

    // The features no active location of the company has.
    public static async Task<HashSet<CompanyFeature>> GetDisabledAsync(ApplicationDbContext ctx, int companyId)
    {
        List<CompanyFeature> enabled = await EnabledForCompany(ctx, companyId)
            .AsNoTracking()
            .TagWithCallSite()
            .Select(x => x.Feature)
            .Distinct()
            .ToListAsync();

        return [.. System.Enum.GetValues<CompanyFeature>().Except(enabled)];
    }

    // The features switched off (or never switched on) at one location.
    public static async Task<HashSet<CompanyFeature>> GetDisabledForLocationAsync(ApplicationDbContext ctx, int locationId, CancellationToken cancellationToken = default)
    {
        List<CompanyFeature> enabled = await ctx.LocationFeatureSettings
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.LocationId == locationId && x.IsEnabled)
            .Select(x => x.Feature)
            .ToListAsync(cancellationToken);

        return [.. System.Enum.GetValues<CompanyFeature>().Except(enabled)];
    }

    public static async Task<bool> IsEnabledForCompanyAsync(ApplicationDbContext ctx, int companyId, CompanyFeature feature, CancellationToken cancellationToken = default)
    {
        return await EnabledForCompany(ctx, companyId)
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(x => x.Feature == feature, cancellationToken);
    }

    public static async Task<bool> IsEnabledForLocationAsync(ApplicationDbContext ctx, int locationId, CompanyFeature feature, CancellationToken cancellationToken = default)
    {
        return await ctx.LocationFeatureSettings
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(x => x.LocationId == locationId && x.Feature == feature && x.IsEnabled, cancellationToken);
    }

    // The locations (of any company) with the feature switched on.
    public static async Task<HashSet<int>> GetEnabledLocationIdsAsync(ApplicationDbContext ctx, CompanyFeature feature, CancellationToken cancellationToken = default)
    {
        List<int> ids = await ctx.LocationFeatureSettings
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.Feature == feature && x.IsEnabled)
            .Select(x => x.LocationId)
            .ToListAsync(cancellationToken);

        return [.. ids];
    }
}
