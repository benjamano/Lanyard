using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.RegularExpressions;

namespace Lanyard.Application.Services.Locations;

// Companies and locations aren't tenant-filtered rows (login has to list every company), so the
// company boundary is enforced here instead: a signed-in user may only see and manage their own
// company's locations and memberships, and only platform admins can create, deactivate or reach
// other companies. A null tenant (tests, system callers) is unrestricted.
//
// Branding is read on every page, every new circuit and every logo request, so it is cached per
// company (see BrandingCache) and evicted by every write in here that changes it.
public class CompanyLocationService(IDbContextFactory<ApplicationDbContext> factory, IMemoryCache cache, ITenantContext? tenant = null) : ICompanyLocationService
{
    private const string NoAccessToCompany = "You do not have access to that company.";

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IMemoryCache _cache = cache;
    private readonly ITenantContext? _tenant = tenant;

    private int? ManageableCompanyId => _tenant?.ManageableCompanyId;

    private bool CanManageCompany(int companyId) => _tenant is null || _tenant.CanManageCompany(companyId);

    private bool CanManageAllCompanies => ManageableCompanyId is null;

    public async Task<Result<List<Company>>> GetCompaniesAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<Company> query = ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive);

            if (ManageableCompanyId is int onlyCompanyId)
            {
                query = query.Where(x => x.Id == onlyCompanyId);
            }

            List<Company> companies = await query
                .OrderBy(x => x.Name)
                .ToListAsync();

            return Result<List<Company>>.Ok(companies);
        }
        catch (Exception ex)
        {
            return Result<List<Company>>.Fail($"Failed to retrieve companies: {ex.Message}");
        }
    }

    public async Task<Result<List<Company>>> GetSessionCompaniesAsync()
    {
        Result<List<Company>> companies = await GetCompaniesAsync();

        if (!companies.IsSuccess || _tenant is null || _tenant.IsSystem)
        {
            return companies;
        }

        return Result<List<Company>>.Ok(companies.Data!.Where(x => x.Id == _tenant.CompanyId).ToList());
    }

    public async Task<Result<Company>> SaveCompanyAsync(Company company)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(company.Name))
            {
                return Result<Company>.Fail("Company name is required.");
            }

            string? normalizedColor = string.IsNullOrWhiteSpace(company.ThemeColorHex) ? null : company.ThemeColorHex.Trim();

            if (normalizedColor is not null && !Regex.IsMatch(normalizedColor, "^#[0-9A-Fa-f]{6}$"))
            {
                return Result<Company>.Fail("Theme color must be a hex value like #C8102E.");
            }

            if (company.Id == 0 ? !CanManageAllCompanies : !CanManageCompany(company.Id))
            {
                return Result<Company>.Fail(NoAccessToCompany);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Company? existing = company.Id == 0 ? null : await ctx.Companies.FirstOrDefaultAsync(x => x.Id == company.Id);

            Company target;

            if (existing is null)
            {
                target = new Company
                {
                    Name = company.Name.Trim(),
                    IsActive = true,
                    CreateDate = DateTime.UtcNow,
                    UpdateDate = DateTime.UtcNow,
                    ThemeColorHex = normalizedColor,
                    LogoFileId = company.LogoFileId,
                    BackgroundImageFileId = company.BackgroundImageFileId,
                    SeasonalThemeSetting = company.SeasonalThemeSetting
                };
                ctx.Companies.Add(target);
            }
            else
            {
                target = existing;
                target.Name = company.Name.Trim();
                target.UpdateDate = DateTime.UtcNow;
                // Full replacement, not a patch: the edit form round-trips the company's complete
                // current branding into its fields before a save, so a null/blank arriving here
                // means "the admin cleared it", not "the caller omitted it".
                target.ThemeColorHex = normalizedColor;
                target.LogoFileId = company.LogoFileId;
                target.BackgroundImageFileId = company.BackgroundImageFileId;
                target.SeasonalThemeSetting = company.SeasonalThemeSetting;
            }

            await ctx.SaveChangesAsync();

            await EvictCompanyAsync(ctx, target.Id);

            return Result<Company>.Ok(target);
        }
        catch (Exception ex)
        {
            return Result<Company>.Fail($"Failed to save company: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateCompanyAsync(int companyId)
    {
        try
        {
            if (!CanManageAllCompanies)
            {
                return Result<bool>.Fail(NoAccessToCompany);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Company? company = await ctx.Companies.FirstOrDefaultAsync(x => x.Id == companyId);

            if (company is null)
            {
                return Result<bool>.Fail("Company not found.");
            }

            company.IsActive = false;
            company.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            await EvictCompanyAsync(ctx, companyId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to deactivate company: {ex.Message}");
        }
    }

    public async Task<Result<List<Location>>> GetLocationsAsync(int? companyId = null)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<Location> query = ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Company)
                .Where(x => x.IsActive);

            if (companyId.HasValue)
            {
                query = query.Where(x => x.CompanyId == companyId.Value);
            }

            if (ManageableCompanyId is int onlyCompanyId)
            {
                query = query.Where(x => x.CompanyId == onlyCompanyId);
            }

            List<Location> locations = await query.OrderBy(x => x.Name).ToListAsync();

            return Result<List<Location>>.Ok(locations);
        }
        catch (Exception ex)
        {
            return Result<List<Location>>.Fail($"Failed to retrieve locations: {ex.Message}");
        }
    }

    public async Task<Result<Location>> SaveLocationAsync(Location location)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(location.Name))
            {
                return Result<Location>.Fail("Location name is required.");
            }

            if (!CanManageCompany(location.CompanyId))
            {
                return Result<Location>.Fail(NoAccessToCompany);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool companyExists = await ctx.Companies.AnyAsync(x => x.Id == location.CompanyId);

            if (!companyExists)
            {
                return Result<Location>.Fail("Company not found.");
            }

            bool nameTaken = await ctx.Locations.AnyAsync(x =>
                x.CompanyId == location.CompanyId && x.Id != location.Id && x.Name == location.Name.Trim());

            if (nameTaken)
            {
                return Result<Location>.Fail("This company already has a location with that name.");
            }

            Location? existing = location.Id == 0 ? null : await ctx.Locations.FirstOrDefaultAsync(x => x.Id == location.Id);

            if (existing is not null && existing.CompanyId != location.CompanyId)
            {
                return Result<Location>.Fail("A location can't be moved to another company.");
            }

            Location target;

            if (existing is null)
            {
                target = new Location
                {
                    CompanyId = location.CompanyId,
                    Name = location.Name.Trim(),
                    IsActive = true,
                    CreateDate = DateTime.UtcNow,
                    UpdateDate = DateTime.UtcNow
                };
                ctx.Locations.Add(target);
            }
            else
            {
                target = existing;
                target.Name = location.Name.Trim();
                target.UpdateDate = DateTime.UtcNow;
            }

            await ctx.SaveChangesAsync();

            _cache.Remove(BrandingCache.LocationScope(target.Id));

            return Result<Location>.Ok(target);
        }
        catch (Exception ex)
        {
            return Result<Location>.Fail($"Failed to save location: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateLocationAsync(int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations.FirstOrDefaultAsync(x => x.Id == locationId);

            if (location is null)
            {
                return Result<bool>.Fail("Location not found.");
            }

            if (!CanManageCompany(location.CompanyId))
            {
                return Result<bool>.Fail(NoAccessToCompany);
            }

            location.IsActive = false;
            location.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            _cache.Remove(BrandingCache.LocationScope(locationId));

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to deactivate location: {ex.Message}");
        }
    }

    public async Task<Result<List<Location>>> GetLocationsForUserAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<Location> locations = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location!)
                    .ThenInclude(x => x.Company)
                .Where(x => x.UserId == userId && x.Location!.IsActive)
                .Where(x => ManageableCompanyId == null || x.Location!.CompanyId == ManageableCompanyId)
                .Select(x => x.Location!)
                .OrderBy(x => x.Name)
                .ToListAsync();

            return Result<List<Location>>.Ok(locations);
        }
        catch (Exception ex)
        {
            return Result<List<Location>>.Fail($"Failed to retrieve locations for user: {ex.Message}");
        }
    }

    public async Task<Result<List<UserProfile>>> GetUsersInLocationAsync(int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<UserProfile> users = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId)
                .Where(x => ManageableCompanyId == null || x.Location!.CompanyId == ManageableCompanyId)
                .Select(x => x.User!)
                .OrderBy(x => x.LastName)
                .ToListAsync();

            return Result<List<UserProfile>>.Ok(users);
        }
        catch (Exception ex)
        {
            return Result<List<UserProfile>>.Fail($"Failed to retrieve users in location: {ex.Message}");
        }
    }

    public async Task<Result<bool>> AddUserToLocationAsync(string userId, int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool userExists = await ctx.Users.AnyAsync(x => x.Id == userId);

            if (!userExists)
            {
                return Result<bool>.Fail("User not found.");
            }

            int? locationCompanyId = await ctx.Locations
                .Where(x => x.Id == locationId)
                .Select(x => (int?)x.CompanyId)
                .FirstOrDefaultAsync();

            if (locationCompanyId is null)
            {
                return Result<bool>.Fail("Location not found.");
            }

            if (!CanManageCompany(locationCompanyId.Value))
            {
                return Result<bool>.Fail(NoAccessToCompany);
            }

            // Otherwise a company admin could pull another company's user (say, its admin) into
            // one of their own locations and then manage that account.
            if (ManageableCompanyId is int onlyCompanyId
                && await ctx.UserLocationMemberships.AnyAsync(x => x.UserId == userId && x.Location!.CompanyId != onlyCompanyId))
            {
                return Result<bool>.Fail("This user belongs to another company.");
            }

            bool alreadyMember = await ctx.UserLocationMemberships.AnyAsync(x => x.UserId == userId && x.LocationId == locationId);

            if (alreadyMember)
            {
                return Result<bool>.Fail("This user is already a member of that location.");
            }

            ctx.UserLocationMemberships.Add(new UserLocationMembership
            {
                UserId = userId,
                LocationId = locationId,
                CreateDate = DateTime.UtcNow
            });

            await ctx.SaveChangesAsync();

            _cache.Remove(BrandingCache.UserCompany(userId));

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to add user to location: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveUserFromLocationAsync(string userId, int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            UserLocationMembership? membership = await ctx.UserLocationMemberships
                .Include(x => x.Location)
                .FirstOrDefaultAsync(x => x.UserId == userId && x.LocationId == locationId);

            if (membership is null)
            {
                return Result<bool>.Fail("This user is not a member of that location.");
            }

            if (!CanManageCompany(membership.Location!.CompanyId))
            {
                return Result<bool>.Fail(NoAccessToCompany);
            }

            ctx.UserLocationMemberships.Remove(membership);
            await ctx.SaveChangesAsync();

            _cache.Remove(BrandingCache.UserCompany(userId));

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to remove user from location: {ex.Message}");
        }
    }

    public async Task<Result<List<int>>> GetCompanyIdsForUserAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<int> companyIds = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == userId && x.Location!.IsActive)
                .Select(x => x.Location!.CompanyId)
                .Distinct()
                .ToListAsync();

            return Result<List<int>>.Ok(companyIds);
        }
        catch (Exception ex)
        {
            return Result<List<int>>.Fail($"Failed to retrieve companies for user: {ex.Message}");
        }
    }

    public async Task<Result<bool>> IsUserMemberOfLocationAsync(string userId, int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool isMember = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.UserId == userId && x.LocationId == locationId);

            return Result<bool>.Ok(isMember);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to check location membership: {ex.Message}");
        }
    }

    public async Task<Result<CompanyBrandingInfo>> GetCompanyBrandingAsync(int companyId)
    {
        try
        {
            if (_cache.TryGetValue(BrandingCache.Branding(companyId), out CompanyBrandingInfo? cached) && cached is not null)
            {
                return Result<CompanyBrandingInfo>.Ok(cached);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CompanyBrandingInfo? branding = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == companyId && x.IsActive)
                .Select(x => new CompanyBrandingInfo(x.Id, x.Name, x.ThemeColorHex, x.LogoFileId, x.BackgroundImageFileId, x.SeasonalThemeSetting))
                .FirstOrDefaultAsync();

            // Only hits are cached: anonymous callers choose the id (?company= on the login page,
            // the logo URLs), so caching misses would let anyone fill memory with junk ids.
            if (branding is null)
            {
                return Result<CompanyBrandingInfo>.Fail("Company not found.");
            }

            _cache.Set(BrandingCache.Branding(companyId), branding, BrandingCache.Ttl);

            return Result<CompanyBrandingInfo>.Ok(branding);
        }
        catch (Exception ex)
        {
            return Result<CompanyBrandingInfo>.Fail($"Failed to retrieve company branding: {ex.Message}");
        }
    }

    public async Task<Result<CompanyBrandingInfo>> GetCompanyBrandingForUserAsync(string userId)
    {
        try
        {
            if (!_cache.TryGetValue(BrandingCache.UserCompany(userId), out int companyId))
            {
                await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

                List<int> companyIds = await ctx.UserLocationMemberships
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(x => x.UserId == userId && x.Location!.IsActive && x.Location.Company!.IsActive)
                    .Select(x => x.Location!.CompanyId)
                    .Distinct()
                    .Take(2)
                    .ToListAsync();

                if (companyIds.Count == 0)
                {
                    return Result<CompanyBrandingInfo>.Fail("User does not belong to an active company.");
                }

                // Belonging to two companies at once has no single right answer, so rather than
                // pick one arbitrarily the caller is told to fall back to another signal.
                if (companyIds.Count > 1)
                {
                    return Result<CompanyBrandingInfo>.Fail("User belongs to more than one company.");
                }

                companyId = companyIds[0];
                _cache.Set(BrandingCache.UserCompany(userId), companyId, BrandingCache.UserCompanyTtl);
            }

            return await GetCompanyBrandingAsync(companyId);
        }
        catch (Exception ex)
        {
            return Result<CompanyBrandingInfo>.Fail($"Failed to retrieve company branding for user: {ex.Message}");
        }
    }

    public async Task<Result<CompanyBrandingInfo>> GetCompanyBrandingForLocationAsync(int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            int? companyId = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == locationId && x.IsActive && x.Company!.IsActive)
                .Select(x => (int?)x.CompanyId)
                .FirstOrDefaultAsync();

            if (companyId is null)
            {
                return Result<CompanyBrandingInfo>.Fail("Location or company not found.");
            }

            return await GetCompanyBrandingAsync(companyId.Value);
        }
        catch (Exception ex)
        {
            return Result<CompanyBrandingInfo>.Fail($"Failed to retrieve company branding for location: {ex.Message}");
        }
    }

    // After a company write: its branding, and its locations' scope details (they carry the
    // company name). Only ever called once SaveChanges has succeeded.
    private async Task EvictCompanyAsync(ApplicationDbContext ctx, int companyId)
    {
        List<int> locationIds = await ctx.Locations
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.CompanyId == companyId)
            .Select(x => x.Id)
            .ToListAsync();

        BrandingCache.EvictCompany(_cache, companyId, locationIds);
    }

    public async Task<Result<List<LoginLocationOption>>> GetLoginLocationOptionsAsync(int? companyId = null)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<Location> query = ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive && x.Company!.IsActive);

            if (companyId.HasValue)
            {
                query = query.Where(x => x.CompanyId == companyId.Value);
            }

            var locations = await query
                .OrderBy(x => x.Company!.Name).ThenBy(x => x.Name)
                .Select(x => new { x.Id, x.Name, x.CompanyId, CompanyName = x.Company!.Name, x.Company.ThemeColorHex, x.Company.LogoFileId })
                .ToListAsync();

            // Same display name as Location.GetDisplayName(), without loading the whole company row.
            List<LoginLocationOption> options = [.. locations.Select(x => new LoginLocationOption(
                x.Id, $"{x.CompanyName} {x.Name}".Trim(), x.CompanyId, x.ThemeColorHex, x.LogoFileId))];

            return Result<List<LoginLocationOption>>.Ok(options);
        }
        catch (Exception ex)
        {
            return Result<List<LoginLocationOption>>.Fail($"Failed to retrieve login locations: {ex.Message}");
        }
    }

    public async Task<Result<List<LoginCompanyOption>>> GetLoginCompanyOptionsAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<LoginCompanyOption> options = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                // The demo company is reached from the homepage's one-click demo buttons, not by
                // picking it and typing a password.
                .Where(x => x.IsActive && !x.IsDemo && x.Locations.Any(l => l.IsActive))
                .OrderBy(x => x.Name)
                .Select(x => new LoginCompanyOption(x.Id, x.Name, x.ThemeColorHex, x.LogoFileId, x.BackgroundImageFileId))
                .ToListAsync();

            return Result<List<LoginCompanyOption>>.Ok(options);
        }
        catch (Exception ex)
        {
            return Result<List<LoginCompanyOption>>.Fail($"Failed to retrieve login companies: {ex.Message}");
        }
    }
}
