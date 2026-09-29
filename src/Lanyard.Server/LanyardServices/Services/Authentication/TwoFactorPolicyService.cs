using Lanyard.Application.Services.Demo;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Authentication;

public class TwoFactorPolicyService(
    IDbContextFactory<ApplicationDbContext> factory,
    ICurrentLocationContext locationContext,
    ICurrentUserAccessor currentUserAccessor,
    AuthenticationStateProvider authStateProvider,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<TwoFactorPolicyService> logger,
    IDemoGuard? demoGuard = null) : ITwoFactorPolicyService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ICurrentLocationContext _locationContext = locationContext;
    private readonly ICurrentUserAccessor _currentUserAccessor = currentUserAccessor;
    private readonly AuthenticationStateProvider _authStateProvider = authStateProvider;
    private readonly IMemoryCache _cache = cache;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<TwoFactorPolicyService> _logger = logger;
    private readonly IDemoGuard? _demoGuard = demoGuard;

    // The route gate asks on every navigation. The cache is shared across circuits and cleared on
    // save, so switching the policy on takes effect on everyone's next navigation in this process;
    // the TTL only bounds staleness from a change made on another instance.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static string CacheKey(int companyId) => $"company-two-factor-required:{companyId}";

    // This service is scoped (one per circuit), and 2FA can't be switched off while the company
    // requires it, so once a user is seen with it on there's no need to ask the database again.
    private string? _userKnownToHaveTwoFactor;

    public async Task<Result<TwoFactorPolicyDto>> GetPolicyForCurrentCompanyAsync()
    {
        try
        {
            Result<int> companyId = await GetManagedCompanyIdAsync();

            if (!companyId.IsSuccess)
            {
                return Result<TwoFactorPolicyDto>.Fail(companyId.Error!);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Company? company = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == companyId.Data && x.IsActive);

            if (company is null)
            {
                return Result<TwoFactorPolicyDto>.Fail("Company not found.");
            }

            List<TwoFactorUserStatusDto> users = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(u => u.Id != ApplicationDbContext.SystemDeletedUserPlaceholderId
                    && ctx.UserLocationMemberships.Any(m => m.UserId == u.Id && m.Location!.CompanyId == company.Id))
                .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
                .Select(u => new TwoFactorUserStatusDto(u.Id, (u.FirstName + " " + u.LastName).Trim(), u.TwoFactorEnabled))
                .ToListAsync();

            string? requiredByName = company.TwoFactorRequiredByUserId is null
                ? null
                : await ctx.Users
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(u => u.Id == company.TwoFactorRequiredByUserId)
                    .Select(u => (u.FirstName + " " + u.LastName).Trim())
                    .FirstOrDefaultAsync();

            return Result<TwoFactorPolicyDto>.Ok(new TwoFactorPolicyDto
            {
                CompanyId = company.Id,
                CompanyName = company.Name,
                IsRequired = company.TwoFactorRequiredSince is not null,
                RequiredSince = company.TwoFactorRequiredSince,
                RequiredByName = requiredByName,
                Users = users
            });
        }
        catch (Exception ex)
        {
            return Result<TwoFactorPolicyDto>.Fail($"Failed to load the two-factor settings: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetRequiredForCurrentCompanyAsync(bool isRequired)
    {
        try
        {
            // Everyone shares the demo accounts and can't set 2FA up there, so requiring it would
            // lock every visitor out.
            if (_demoGuard is not null && await _demoGuard.IsDemoSessionAsync())
            {
                return Result<bool>.Fail(DemoGuard.NotInDemoMessage);
            }

            Result<int> companyId = await GetManagedCompanyIdAsync();

            if (!companyId.IsSuccess)
            {
                return Result<bool>.Fail(companyId.Error!);
            }

            Result<string> userId = await _currentUserAccessor.GetCurrentUserIdAsync();

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Company? company = await ctx.Companies
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == companyId.Data && x.IsActive);

            if (company is null)
            {
                return Result<bool>.Fail("Company not found.");
            }

            if ((company.TwoFactorRequiredSince is not null) == isRequired)
            {
                return Result<bool>.Ok(true);
            }

            company.TwoFactorRequiredSince = isRequired ? _timeProvider.GetUtcNow().UtcDateTime : null;
            company.TwoFactorRequiredByUserId = isRequired ? userId.Data : null;
            company.UpdateDate = _timeProvider.GetUtcNow().UtcDateTime;

            await ctx.SaveChangesAsync();

            _cache.Remove(CacheKey(company.Id));

            _logger.LogInformation("{UserId} made two-factor authentication {State} for company {CompanyId}",
                userId.Data, isRequired ? "required" : "optional", company.Id);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to save the two-factor setting: {ex.Message}");
        }
    }

    public async Task<Result<bool>> IsRequiredForCurrentUserAsync()
    {
        try
        {
            AuthenticationState authState = await _authStateProvider.GetAuthenticationStateAsync();

            if (authState.User?.Identity?.IsAuthenticated != true)
            {
                return Result<bool>.Ok(false);
            }

            if (_demoGuard is not null && await _demoGuard.IsDemoSessionAsync())
            {
                return Result<bool>.Ok(false);
            }

            Result<LocationScope> scope = await _locationContext.GetScopeAsync();

            // An Admin signed in without a location has no company for this session, so there is
            // no policy to apply to them.
            if (!scope.IsSuccess || scope.Data?.CompanyId is not int companyId)
            {
                return Result<bool>.Ok(false);
            }

            return Result<bool>.Ok(await IsRequiredForCompanyAsync(companyId));
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to check the two-factor setting: {ex.Message}");
        }
    }

    public async Task<bool> IsSetupRequiredForCurrentUserAsync()
    {
        try
        {
            Result<bool> required = await IsRequiredForCurrentUserAsync();

            if (!required.IsSuccess)
            {
                _logger.LogWarning("Skipping the required two-factor check because it failed: {Error}", required.Error);
                return false;
            }

            if (!required.Data)
            {
                return false;
            }

            Result<string> userId = await _currentUserAccessor.GetCurrentUserIdAsync();

            if (!userId.IsSuccess || userId.Data is null)
            {
                return false;
            }

            if (_userKnownToHaveTwoFactor == userId.Data)
            {
                return false;
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool hasTwoFactor = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(u => u.Id == userId.Data)
                .Select(u => u.TwoFactorEnabled)
                .FirstOrDefaultAsync();

            if (hasTwoFactor)
            {
                _userKnownToHaveTwoFactor = userId.Data;
            }

            return !hasTwoFactor;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping the required two-factor check because it threw");
            return false;
        }
    }

    private async Task<bool> IsRequiredForCompanyAsync(int companyId)
    {
        if (_cache.TryGetValue(CacheKey(companyId), out bool cached))
        {
            return cached;
        }

        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

        bool isRequired = await ctx.Companies
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(x => x.Id == companyId && x.TwoFactorRequiredSince != null);

        _cache.Set(CacheKey(companyId), isRequired, CacheTtl);

        return isRequired;
    }

    // Managers and Admins may change the policy, and only for the company they're signed in to.
    private async Task<Result<int>> GetManagedCompanyIdAsync()
    {
        AuthenticationState authState = await _authStateProvider.GetAuthenticationStateAsync();

        bool isManagerOrAbove = authState.User?.Identity?.IsAuthenticated == true
            && (authState.User.IsInRole("Admin") || authState.User.IsInRole("Manager"));

        if (!isManagerOrAbove)
        {
            return Result<int>.Fail("Only managers and administrators can change the two-factor setting.");
        }

        Result<LocationScope> scope = await _locationContext.GetScopeAsync();

        if (!scope.IsSuccess || scope.Data?.CompanyId is not int companyId)
        {
            return Result<int>.Fail(scope.Error ?? "Sign in to one of your company's locations to change this.");
        }

        return Result<int>.Ok(companyId);
    }
}
