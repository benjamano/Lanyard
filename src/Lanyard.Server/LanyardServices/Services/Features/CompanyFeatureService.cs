using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Features;

public class CompanyFeatureService(
    IDbContextFactory<ApplicationDbContext> factory,
    ICurrentLocationContext locationContext,
    AuthenticationStateProvider authStateProvider,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<CompanyFeatureService> logger,
    ITenantContext? tenant = null) : ICompanyFeatureService
{
    private readonly ITenantContext? _tenant = tenant;
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ICurrentLocationContext _locationContext = locationContext;
    private readonly AuthenticationStateProvider _authStateProvider = authStateProvider;
    private readonly IMemoryCache _cache = cache;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<CompanyFeatureService> _logger = logger;

    // The nav, the route gate and the dashboard grid all ask on every page, for every user. The
    // cache is shared across circuits and cleared on save, so a switch takes effect on the next
    // navigation in this process; the TTL only bounds staleness from edits made elsewhere.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlySet<CompanyFeature> AllFeatures = System.Enum.GetValues<CompanyFeature>().ToHashSet();

    private static string CacheKey(int companyId) => $"company-features-enabled:{companyId}";

    // For writers outside this service that change which locations a company has.
    public static void Evict(IMemoryCache? cache, int companyId) => cache?.Remove(CacheKey(companyId));

    private static string LocationCompanyKey(int locationId) => $"location-feature-company:{locationId}";

    // What each active location of one company has switched on.
    private sealed record CompanySwitches(IReadOnlyList<FeatureLocation> Locations, IReadOnlyDictionary<int, HashSet<CompanyFeature>> EnabledByLocation)
    {
        public IReadOnlySet<CompanyFeature> EnabledAnywhere { get; } =
            EnabledByLocation.Values.SelectMany(x => x).ToHashSet();

        public IReadOnlySet<CompanyFeature> DisabledAt(int locationId) =>
            EnabledByLocation.TryGetValue(locationId, out HashSet<CompanyFeature>? enabled)
                ? AllFeatures.Except(enabled).ToHashSet()
                : AllFeatures;
    }

    public async Task<Result<List<CompanyFeatureState>>> GetFeaturesAsync(int companyId)
    {
        try
        {
            CompanySwitches switches = await GetSwitchesAsync(companyId);

            List<CompanyFeatureState> states = CompanyFeatureCatalog.All
                .Select(x => new CompanyFeatureState(x, switches.EnabledAnywhere.Contains(x.Feature)))
                .ToList();

            return Result<List<CompanyFeatureState>>.Ok(states);
        }
        catch (Exception ex)
        {
            return Result<List<CompanyFeatureState>>.Fail($"Failed to load features: {ex.Message}");
        }
    }

    public async Task<Result<CompanyFeatureGrid>> GetFeatureGridAsync(int companyId)
    {
        try
        {
            CompanySwitches switches = await GetSwitchesAsync(companyId);

            HashSet<(int, CompanyFeature)> enabled = switches.EnabledByLocation
                .SelectMany(x => x.Value.Select(f => (x.Key, f)))
                .ToHashSet();

            return Result<CompanyFeatureGrid>.Ok(new CompanyFeatureGrid(CompanyFeatureCatalog.All, switches.Locations, enabled));
        }
        catch (Exception ex)
        {
            return Result<CompanyFeatureGrid>.Fail($"Failed to load features: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetFeatureEnabledAsync(LocationScope scope, int locationId, CompanyFeature feature, bool isEnabled, string? updatedByUserId)
    {
        try
        {
            if (!scope.IsAdmin)
            {
                return Result<bool>.Fail("Only admins can change which features a location has.");
            }

            // Feature switches are what Lanyard grants a company, so a company's own Admin mustn't
            // be able to switch its features on (or anyone else's).
            if (_tenant is not null && !_tenant.IsSystem && !_tenant.IsPlatformAdmin)
            {
                return Result<bool>.Fail("Only platform admins can change which features a location has.");
            }

            if (!System.Enum.IsDefined(feature))
            {
                return Result<bool>.Fail("Unknown feature.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            int? companyId = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == locationId && x.IsActive && x.Company!.IsActive)
                .Select(x => (int?)x.CompanyId)
                .FirstOrDefaultAsync();

            if (companyId is null)
            {
                return Result<bool>.Fail("Location not found.");
            }

            LocationFeatureSetting? setting = await ctx.LocationFeatureSettings
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.LocationId == locationId && x.Feature == feature);

            if (setting is null)
            {
                setting = new LocationFeatureSetting { LocationId = locationId, Feature = feature };
                ctx.LocationFeatureSettings.Add(setting);
            }

            setting.IsEnabled = isEnabled;
            setting.UpdateDate = _timeProvider.GetUtcNow().UtcDateTime;
            setting.UpdatedByUserId = updatedByUserId;

            await ctx.SaveChangesAsync();

            _cache.Remove(CacheKey(companyId.Value));

            _logger.LogInformation("{UserId} turned {Feature} {State} for location {LocationId} (company {CompanyId})",
                updatedByUserId, feature, isEnabled ? "on" : "off", locationId, companyId.Value);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to save feature: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlySet<CompanyFeature>>> GetDisabledFeaturesForCurrentUserAsync()
    {
        try
        {
            AuthenticationState authState = await _authStateProvider.GetAuthenticationStateAsync();

            if (authState.User?.Identity?.IsAuthenticated != true || authState.User.IsInRole("Admin"))
            {
                return Result<IReadOnlySet<CompanyFeature>>.Ok(new HashSet<CompanyFeature>());
            }

            Result<LocationScope> scope = await _locationContext.GetScopeAsync();

            if (!scope.IsSuccess || scope.Data?.LocationId is not int locationId || scope.Data.CompanyId is not int companyId)
            {
                return Result<IReadOnlySet<CompanyFeature>>.Fail(scope.Error ?? "No location is set for this session.");
            }

            CompanySwitches switches = await GetSwitchesAsync(companyId);

            return Result<IReadOnlySet<CompanyFeature>>.Ok(switches.DisabledAt(locationId));
        }
        catch (Exception ex)
        {
            return Result<IReadOnlySet<CompanyFeature>>.Fail($"Failed to check features: {ex.Message}");
        }
    }

    public async Task<IReadOnlySet<CompanyFeature>> GetHiddenFeaturesForCurrentUserAsync()
    {
        Result<IReadOnlySet<CompanyFeature>> disabled = await GetDisabledFeaturesForCurrentUserAsync();

        if (disabled.IsSuccess)
        {
            return disabled.Data!;
        }

        _logger.LogWarning("Hiding every feature because the check failed: {Error}", disabled.Error);

        return AllFeatures;
    }

    public async Task<bool> IsEnabledForCurrentUserAsync(CompanyFeature feature)
    {
        IReadOnlySet<CompanyFeature> hidden = await GetHiddenFeaturesForCurrentUserAsync();

        return !hidden.Contains(feature);
    }

    public async Task<bool> IsEnabledForCompanyAsync(int companyId, CompanyFeature feature)
    {
        try
        {
            CompanySwitches switches = await GetSwitchesAsync(companyId);

            return switches.EnabledAnywhere.Contains(feature);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Treating {Feature} as off for company {CompanyId} because the check failed", feature, companyId);

            return false;
        }
    }

    public async Task<bool> IsEnabledForLocationAsync(int locationId, CompanyFeature feature)
    {
        try
        {
            int? companyId = await GetCompanyIdForLocationAsync(locationId);

            if (companyId is null)
            {
                return false;
            }

            CompanySwitches switches = await GetSwitchesAsync(companyId.Value);

            return !switches.DisabledAt(locationId).Contains(feature);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Treating {Feature} as off for location {LocationId} because the check failed", feature, locationId);

            return false;
        }
    }

    // A location never moves company, so its company is cached for longer than the switches.
    private async Task<int?> GetCompanyIdForLocationAsync(int locationId)
    {
        if (_cache.TryGetValue(LocationCompanyKey(locationId), out int cached))
        {
            return cached;
        }

        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

        int? companyId = await ctx.Locations
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.Id == locationId)
            .Select(x => (int?)x.CompanyId)
            .FirstOrDefaultAsync();

        if (companyId is int id)
        {
            _cache.Set(LocationCompanyKey(locationId), id, TimeSpan.FromHours(1));
        }

        return companyId;
    }

    private async Task<CompanySwitches> GetSwitchesAsync(int companyId)
    {
        if (_cache.TryGetValue(CacheKey(companyId), out CompanySwitches? cached) && cached is not null)
        {
            return cached;
        }

        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

        List<FeatureLocation> locations = await ctx.Locations
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.CompanyId == companyId && x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new FeatureLocation(x.Id, x.Name))
            .ToListAsync();

        List<int> locationIds = locations.Select(x => x.LocationId).ToList();

        var rows = await ctx.LocationFeatureSettings
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsEnabled && locationIds.Contains(x.LocationId))
            .Select(x => new { x.LocationId, x.Feature })
            .ToListAsync();

        Dictionary<int, HashSet<CompanyFeature>> enabled = locationIds.ToDictionary(
            id => id,
            id => rows.Where(r => r.LocationId == id).Select(r => r.Feature).ToHashSet());

        CompanySwitches switches = new(locations, enabled);

        _cache.Set(CacheKey(companyId), switches, CacheTtl);

        return switches;
    }
}
