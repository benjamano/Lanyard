using Lanyard.Application.Services.Locations;
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
    ILogger<CompanyFeatureService> logger) : ICompanyFeatureService
{
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

    private static string CacheKey(int companyId) => $"company-features-disabled:{companyId}";

    public async Task<Result<List<CompanyFeatureState>>> GetFeaturesAsync(int companyId)
    {
        try
        {
            IReadOnlySet<CompanyFeature> disabled = await GetDisabledForCompanyAsync(companyId);

            List<CompanyFeatureState> states = CompanyFeatureCatalog.All
                .Select(x => new CompanyFeatureState(x, !disabled.Contains(x.Feature)))
                .ToList();

            return Result<List<CompanyFeatureState>>.Ok(states);
        }
        catch (Exception ex)
        {
            return Result<List<CompanyFeatureState>>.Fail($"Failed to load features: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetFeatureEnabledAsync(LocationScope scope, int companyId, CompanyFeature feature, bool isEnabled, string? updatedByUserId)
    {
        try
        {
            if (!scope.IsAdmin)
            {
                return Result<bool>.Fail("Only admins can change which features a company has.");
            }

            if (!System.Enum.IsDefined(feature))
            {
                return Result<bool>.Fail("Unknown feature.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool companyExists = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.Id == companyId && x.IsActive);

            if (!companyExists)
            {
                return Result<bool>.Fail("Company not found.");
            }

            CompanyFeatureSetting? setting = await ctx.CompanyFeatureSettings
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.Feature == feature);

            if (setting is null)
            {
                setting = new CompanyFeatureSetting { CompanyId = companyId, Feature = feature };
                ctx.CompanyFeatureSettings.Add(setting);
            }

            setting.IsEnabled = isEnabled;
            setting.UpdateDate = _timeProvider.GetUtcNow().UtcDateTime;
            setting.UpdatedByUserId = updatedByUserId;

            await ctx.SaveChangesAsync();

            _cache.Remove(CacheKey(companyId));

            _logger.LogInformation("{UserId} turned {Feature} {State} for company {CompanyId}",
                updatedByUserId, feature, isEnabled ? "on" : "off", companyId);

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

            if (!scope.IsSuccess || scope.Data?.CompanyId is not int companyId)
            {
                return Result<IReadOnlySet<CompanyFeature>>.Fail(scope.Error ?? "No company is set for this session.");
            }

            return Result<IReadOnlySet<CompanyFeature>>.Ok(await GetDisabledForCompanyAsync(companyId));
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

        _logger.LogWarning("Hiding every company feature because the check failed: {Error}", disabled.Error);

        return System.Enum.GetValues<CompanyFeature>().ToHashSet();
    }

    public async Task<bool> IsEnabledForCurrentUserAsync(CompanyFeature feature)
    {
        IReadOnlySet<CompanyFeature> hidden = await GetHiddenFeaturesForCurrentUserAsync();

        return !hidden.Contains(feature);
    }

    private async Task<IReadOnlySet<CompanyFeature>> GetDisabledForCompanyAsync(int companyId)
    {
        if (_cache.TryGetValue(CacheKey(companyId), out IReadOnlySet<CompanyFeature>? cached) && cached is not null)
        {
            return cached;
        }

        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

        IReadOnlySet<CompanyFeature> disabled = await CompanyFeatureQueries.GetDisabledAsync(ctx, companyId);

        _cache.Set(CacheKey(companyId), disabled, CacheTtl);

        return disabled;
    }
}
