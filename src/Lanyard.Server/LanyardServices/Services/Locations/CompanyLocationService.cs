using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Enum;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Lanyard.Application.Services.Locations;

public class CompanyLocationService(
    IDbContextFactory<ApplicationDbContext> factory,
    ICompanyAccessService companyAccess) : ICompanyLocationService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    // Checked here rather than only in the page. A Manager can be given the Companies &
    // Locations screen to run their own venue, and hiding another tenant's company from a list
    // is presentation, not a boundary - the boundary has to be where the write happens.
    private readonly ICompanyAccessService _companyAccess = companyAccess;

    /// <summary>Blank and whitespace both mean "not set", so both are stored as null.</summary>
    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

            string? normalizedSecondaryColor = string.IsNullOrWhiteSpace(company.SecondaryColorHex)
                ? null
                : company.SecondaryColorHex.Trim();

            if (normalizedSecondaryColor is not null && !Regex.IsMatch(normalizedSecondaryColor, "^#[0-9A-Fa-f]{6}$"))
            {
                return Result<Company>.Fail("Secondary color must be a hex value like #C8102E.");
            }

            string? normalizedSlug = string.IsNullOrWhiteSpace(company.Slug) ? null : company.Slug.Trim().ToLowerInvariant();

            // Constrained to what can sit in a URL path segment unescaped, since that is the only
            // thing a slug is ever used for.
            if (normalizedSlug is not null && !Regex.IsMatch(normalizedSlug, "^[a-z0-9]+(-[a-z0-9]+)*$"))
            {
                return Result<Company>.Fail("Slug may only contain lowercase letters, numbers and hyphens, for example 'play2day'.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (normalizedSlug is not null
                && await ctx.Companies.AnyAsync(x => x.Slug == normalizedSlug && x.Id != company.Id))
            {
                // Checked rather than left to the unique index so the admin gets a sentence
                // instead of a constraint violation.
                return Result<Company>.Fail($"The slug '{normalizedSlug}' is already used by another company.");
            }

            CompanyAccess access = await _companyAccess.GetCurrentAsync();

            if (company.Id == 0 && !access.CanCreateCompanies)
            {
                return Result<Company>.Fail("You don't have permission to create a company.");
            }

            if (company.Id != 0 && !access.CanAdminister(company.Id))
            {
                return Result<Company>.Fail("You don't have permission to edit this company.");
            }

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
                    SecondaryColorHex = normalizedSecondaryColor,
                    Slug = normalizedSlug,
                    // Not settable here even on create - see SetStripeAccountIdAsync.
                    StripeAccountId = null,
                    LogoFileId = company.LogoFileId,
                    BackgroundImageFileId = company.BackgroundImageFileId,
                    FaviconFileId = company.FaviconFileId
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
                target.SecondaryColorHex = normalizedSecondaryColor;
                target.Slug = normalizedSlug;
                target.LogoFileId = company.LogoFileId;
                target.BackgroundImageFileId = company.BackgroundImageFileId;
                target.FaviconFileId = company.FaviconFileId;
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
            // Admin only, even for a manager's own company: taking a whole tenant offline is not
            // something the tenant should be able to do to itself by accident.
            if (!(await _companyAccess.GetCurrentAsync()).CanCreateCompanies)
            {
                return Result<bool>.Fail("You don't have permission to remove a company.");
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

            // Checked against the company the location is being saved *into*, so a manager
            // cannot move or add a venue under a company they have no rights over.
            if (!await _companyAccess.CanAdministerCompanyAsync(location.CompanyId))
            {
                return Result<Location>.Fail("You don't have permission to manage this company's venues.");
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

    public async Task<Result<bool>> SetLocationOrderingEnabledAsync(int locationId, bool orderingEnabled)
    {
        try
        {
            // Turning a venue's ordering on or off is a write like any other on this page, and
            // was the one that got missed when the rest were scoped.
            if (!await _companyAccess.CanManageVenueOperationsAsync(locationId))
            {
                return Result<bool>.Fail("You don't have permission to change this venue.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations.FirstOrDefaultAsync(x => x.Id == locationId);

            if (location is null)
            {
                return Result<bool>.Fail("Location not found.");
            }

            location.OrderingEnabled = orderingEnabled;
            location.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to update ordering for location: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateLocationAsync(int locationId)
    {
        try
        {
            if (!await _companyAccess.CanAdministerLocationAsync(locationId))
            {
                return Result<bool>.Fail("You don't have permission to remove this venue.");
            }

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

    public async Task<Result<List<LocationOpeningHours>>> GetOpeningHoursAsync(int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<LocationOpeningHours> hours = await ctx.LocationOpeningHours
                .AsNoTracking()
                .TagWithCallSite()
                .Where(h => h.LocationId == locationId)
                .OrderBy(h => h.DayOfWeek)
                .ThenBy(h => h.OpensAt)
                .ToListAsync();

            return Result<List<LocationOpeningHours>>.Ok(hours);
        }
        catch (Exception ex)
        {
            return Result<List<LocationOpeningHours>>.Fail($"Failed to load opening hours: {ex.Message}");
        }
    }

    public async Task<Result<LocationOpeningHours>> AddOpeningHoursAsync(LocationOpeningHours hours)
    {
        try
        {
            if (!await _companyAccess.CanManageVenueOperationsAsync(hours.LocationId))
            {
                return Result<LocationOpeningHours>.Fail("You don't have permission to change this venue.");
            }

            // Equal would mean a window with no time in it, which reads as "open" in the editor
            // and accepts nothing in practice.
            if (hours.ClosesAt <= hours.OpensAt)
            {
                return Result<LocationOpeningHours>.Fail("The closing time has to be after the opening time.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool overlaps = await ctx.LocationOpeningHours.AnyAsync(h =>
                h.LocationId == hours.LocationId
                && h.DayOfWeek == hours.DayOfWeek
                && hours.OpensAt < h.ClosesAt
                && h.OpensAt < hours.ClosesAt);

            if (overlaps)
            {
                // Refused rather than merged: two overlapping windows are almost always a typo,
                // and silently combining them hides which one was wrong.
                return Result<LocationOpeningHours>.Fail("That overlaps a window already set for this day.");
            }

            LocationOpeningHours entity = new()
            {
                LocationId = hours.LocationId,
                DayOfWeek = hours.DayOfWeek,
                OpensAt = hours.OpensAt,
                ClosesAt = hours.ClosesAt,
                CreateDate = DateTime.UtcNow
            };

            await ctx.LocationOpeningHours.AddAsync(entity);
            await ctx.SaveChangesAsync();

            return Result<LocationOpeningHours>.Ok(entity);
        }
        catch (Exception ex)
        {
            return Result<LocationOpeningHours>.Fail($"Failed to add opening hours: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveOpeningHoursAsync(int openingHoursId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            LocationOpeningHours? hours = await ctx.LocationOpeningHours
                .FirstOrDefaultAsync(h => h.Id == openingHoursId);

            if (hours is null)
            {
                return Result<bool>.Ok(true);
            }

            if (!await _companyAccess.CanManageVenueOperationsAsync(hours.LocationId))
            {
                return Result<bool>.Fail("You don't have permission to change this venue.");
            }

            // Hard-deleted, unlike most things here: an opening window is a setting, not a
            // record of anything that happened, and a soft-deleted one would still have to be
            // filtered out of the overlap check above.
            ctx.LocationOpeningHours.Remove(hours);
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to remove opening hours: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetLocationServiceSettingsAsync(
        int locationId, OrderFulfilmentMode fulfilmentMode, Guid? receiptPrinterClientId, string timeZoneId)
    {
        try
        {
            if (!await _companyAccess.CanManageVenueOperationsAsync(locationId))
            {
                return Result<bool>.Fail("You don't have permission to change this venue.");
            }

            // Validated before saving: an unusable zone would silently push every opening window
            // onto UTC, which in British summer is an hour out in the direction of staying open.
            if (string.IsNullOrWhiteSpace(timeZoneId) || !IsKnownTimeZone(timeZoneId))
            {
                return Result<bool>.Fail("That time zone isn't one this server recognises.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations.FirstOrDefaultAsync(l => l.Id == locationId);

            if (location is null)
            {
                return Result<bool>.Fail("Venue not found.");
            }

            // The kiosk has to be one of this venue's own. Permission on the venue is not enough
            // on its own: without this check a kitchen manager could name any kiosk on the
            // platform and have this venue's tickets - table, dishes, allergens and the
            // customer's free-text note - come out of another company's printer.
            if (receiptPrinterClientId is Guid printerClientId)
            {
                bool kioskIsAtThisVenue = await ctx.Clients
                    .AsNoTracking()
                    .TagWithCallSite()
                    .AnyAsync(c => c.Id == printerClientId && c.LocationId == locationId);

                if (!kioskIsAtThisVenue)
                {
                    return Result<bool>.Fail("That kiosk isn't assigned to this venue.");
                }
            }

            location.FulfilmentMode = fulfilmentMode;
            location.ReceiptPrinterClientId = receiptPrinterClientId;
            location.TimeZoneId = timeZoneId;
            location.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to save the venue's service settings: {ex.Message}");
        }
    }

    private static bool IsKnownTimeZone(string timeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
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

            return Result<CompanyBrandingInfo>.Ok(new CompanyBrandingInfo(company.Id, company.ThemeColorHex, company.LogoFileId, company.BackgroundImageFileId, company.FaviconFileId));
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
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<Company> companies = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == userId && x.Location!.IsActive && x.Location.Company!.IsActive)
                .Select(x => x.Location!.Company!)
                .Distinct()
                .ToListAsync();

            if (companies.Count == 0)
            {
                return Result<CompanyBrandingInfo>.Fail("User does not belong to an active company.");
            }

            // Belonging to two companies at once has no single right answer, so rather than
            // pick one arbitrarily the caller is told to fall back to another signal.
            if (companies.Count > 1)
            {
                return Result<CompanyBrandingInfo>.Fail("User belongs to more than one company.");
            }

            Company company = companies[0];

            return Result<CompanyBrandingInfo>.Ok(new CompanyBrandingInfo(
                company.Id, company.ThemeColorHex, company.LogoFileId, company.BackgroundImageFileId, company.FaviconFileId));
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

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Company)
                .FirstOrDefaultAsync(x => x.Id == locationId && x.IsActive && x.Company!.IsActive);

            if (location?.Company is null)
            {
                return Result<CompanyBrandingInfo>.Fail("Location or company not found.");
            }

            return Result<CompanyBrandingInfo>.Ok(new CompanyBrandingInfo(location.Company.Id, location.Company.ThemeColorHex, location.Company.LogoFileId, location.Company.BackgroundImageFileId, location.Company.FaviconFileId));
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
