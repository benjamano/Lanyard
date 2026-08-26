using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Lanyard.Application.Services.Locations;

public class CompanyLocationService(IDbContextFactory<ApplicationDbContext> factory) : ICompanyLocationService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<List<Company>>> GetCompaniesAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<Company> companies = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive)
                .OrderBy(x => x.Name)
                .ToListAsync();

            return Result<List<Company>>.Ok(companies);
        }
        catch (Exception ex)
        {
            return Result<List<Company>>.Fail($"Failed to retrieve companies: {ex.Message}");
        }
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
                    BackgroundImageFileId = company.BackgroundImageFileId
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
            }

            await ctx.SaveChangesAsync();

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
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Company? company = await ctx.Companies.FirstOrDefaultAsync(x => x.Id == companyId);

            if (company is null)
            {
                return Result<bool>.Fail("Company not found.");
            }

            company.IsActive = false;
            company.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

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

            location.IsActive = false;
            location.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

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

            bool locationExists = await ctx.Locations.AnyAsync(x => x.Id == locationId);

            if (!locationExists)
            {
                return Result<bool>.Fail("Location not found.");
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
                .FirstOrDefaultAsync(x => x.UserId == userId && x.LocationId == locationId);

            if (membership is null)
            {
                return Result<bool>.Fail("This user is not a member of that location.");
            }

            ctx.UserLocationMemberships.Remove(membership);
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to remove user from location: {ex.Message}");
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
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Company? company = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == companyId && x.IsActive);

            if (company is null)
            {
                return Result<CompanyBrandingInfo>.Fail("Company not found.");
            }

            return Result<CompanyBrandingInfo>.Ok(new CompanyBrandingInfo(company.Id, company.ThemeColorHex, company.LogoFileId, company.BackgroundImageFileId));
        }
        catch (Exception ex)
        {
            return Result<CompanyBrandingInfo>.Fail($"Failed to retrieve company branding: {ex.Message}");
        }
    }

    public async Task<Result<CompanyBrandingInfo>> GetCompanyBrandingForLocationAsync(int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Company)
                .FirstOrDefaultAsync(x => x.Id == locationId && x.IsActive && x.Company!.IsActive);

            if (location?.Company is null)
            {
                return Result<CompanyBrandingInfo>.Fail("Location or company not found.");
            }

            return Result<CompanyBrandingInfo>.Ok(new CompanyBrandingInfo(location.Company.Id, location.Company.ThemeColorHex, location.Company.LogoFileId, location.Company.BackgroundImageFileId));
        }
        catch (Exception ex)
        {
            return Result<CompanyBrandingInfo>.Fail($"Failed to retrieve company branding for location: {ex.Message}");
        }
    }

    public async Task<Result<List<LoginLocationOption>>> GetLoginLocationOptionsAsync(int? companyId = null)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            IQueryable<Location> query = ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Company)
                .Where(x => x.IsActive && x.Company!.IsActive);

            if (companyId.HasValue)
            {
                query = query.Where(x => x.CompanyId == companyId.Value);
            }

            List<Location> locations = await query
                .OrderBy(x => x.Company!.Name).ThenBy(x => x.Name)
                .ToListAsync();

            List<LoginLocationOption> options = [.. locations.Select(x => new LoginLocationOption(
                x.Id, x.GetDisplayName(), x.CompanyId, x.Company!.ThemeColorHex, x.Company!.LogoFileId))];

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

            List<Company> companies = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive && x.Locations.Any(l => l.IsActive))
                .OrderBy(x => x.Name)
                .ToListAsync();

            List<LoginCompanyOption> options = [.. companies.Select(x =>
                new LoginCompanyOption(x.Id, x.Name, x.ThemeColorHex, x.LogoFileId, x.BackgroundImageFileId))];

            return Result<List<LoginCompanyOption>>.Ok(options);
        }
        catch (Exception ex)
        {
            return Result<List<LoginCompanyOption>>.Fail($"Failed to retrieve login companies: {ex.Message}");
        }
    }
}
