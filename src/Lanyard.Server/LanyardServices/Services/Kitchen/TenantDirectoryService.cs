using System.Text.RegularExpressions;
using Lanyard.Infrastructure.Branding;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Kitchen;

public class TenantDirectoryService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<TenantDirectoryService> logger) : ITenantDirectoryService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<TenantDirectoryService> _logger = logger;

    // Hostnames only: no scheme, no port, no path. Rejecting anything else on write is what
    // lets the read path be a plain equality match on a lowercase string.
    private static readonly Regex HostnamePattern = new(
        @"^(?=.{1,253}$)(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))*$",
        RegexOptions.Compiled);

    public async Task<Result<TenantBrandingDto>> GetTenantByHostnameAsync(string hostname)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hostname))
            {
                return Result<TenantBrandingDto>.Fail("Hostname is required.");
            }

            string normalized = NormalizeHostname(hostname);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Company? company = await ctx.CompanyDomains
                .AsNoTracking()
                .TagWithCallSite()
                .Where(d => d.Hostname == normalized && d.IsActive)
                .Select(d => d.Company)
                .FirstOrDefaultAsync(c => c != null && c.IsActive);

            if (company is null)
            {
                return Result<TenantBrandingDto>.Fail($"No active tenant is mapped to host '{normalized}'.");
            }

            return Result<TenantBrandingDto>.Ok(await BuildBrandingAsync(ctx, company));
        }
        catch (Exception ex)
        {
            return Result<TenantBrandingDto>.Fail($"Failed to resolve tenant by hostname: {ex.Message}");
        }
    }

    public async Task<Result<TenantBrandingDto>> GetTenantBySlugAsync(string slug)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return Result<TenantBrandingDto>.Fail("Slug is required.");
            }

            string normalized = slug.Trim().ToLowerInvariant();

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Company? company = await ctx.Companies
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(c => c.Slug == normalized && c.IsActive);

            if (company is null)
            {
                return Result<TenantBrandingDto>.Fail($"No active tenant with slug '{normalized}'.");
            }

            return Result<TenantBrandingDto>.Ok(await BuildBrandingAsync(ctx, company));
        }
        catch (Exception ex)
        {
            return Result<TenantBrandingDto>.Fail($"Failed to resolve tenant by slug: {ex.Message}");
        }
    }

    public async Task<Result<List<CompanyDomain>>> GetDomainsForCompanyAsync(int companyId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<CompanyDomain> domains = await ctx.CompanyDomains
                .AsNoTracking()
                .TagWithCallSite()
                .Where(d => d.CompanyId == companyId && d.IsActive)
                .OrderByDescending(d => d.IsPrimary)
                .ThenBy(d => d.Hostname)
                .ToListAsync();

            return Result<List<CompanyDomain>>.Ok(domains);
        }
        catch (Exception ex)
        {
            return Result<List<CompanyDomain>>.Fail($"Failed to retrieve company domains: {ex.Message}");
        }
    }

    public async Task<Result<CompanyDomain>> SaveDomainAsync(CompanyDomain domain)
    {
        try
        {
            string normalized = NormalizeHostname(domain.Hostname ?? string.Empty);

            if (!HostnamePattern.IsMatch(normalized))
            {
                return Result<CompanyDomain>.Fail("Enter a bare hostname, for example 'play2day.co.uk' - no scheme, port or path.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (!await ctx.Companies.AnyAsync(c => c.Id == domain.CompanyId && c.IsActive))
            {
                return Result<CompanyDomain>.Fail("Company not found.");
            }

            // Checked explicitly rather than relying on the unique index, so the admin sees which
            // company already claims the host instead of a raw constraint violation.
            CompanyDomain? clash = await ctx.CompanyDomains
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(d => d.Hostname == normalized && d.Id != domain.Id);

            if (clash is not null)
            {
                return clash.CompanyId == domain.CompanyId
                    ? Result<CompanyDomain>.Fail($"'{normalized}' is already registered for this company.")
                    : Result<CompanyDomain>.Fail($"'{normalized}' is already registered to another company.");
            }

            DateTime now = DateTime.UtcNow;
            CompanyDomain entity;

            if (domain.Id == 0)
            {
                entity = new CompanyDomain
                {
                    CompanyId = domain.CompanyId,
                    Hostname = normalized,
                    IsPrimary = domain.IsPrimary,
                    IsActive = true,
                    CreateDate = now,
                    UpdateDate = now
                };

                await ctx.CompanyDomains.AddAsync(entity);
            }
            else
            {
                CompanyDomain? existing = await ctx.CompanyDomains.FirstOrDefaultAsync(d => d.Id == domain.Id);

                if (existing is null)
                {
                    return Result<CompanyDomain>.Fail("Domain not found.");
                }

                existing.Hostname = normalized;
                existing.IsPrimary = domain.IsPrimary;
                existing.UpdateDate = now;
                entity = existing;
            }

            // Exactly one primary per company: absolute URLs built from it (printed QR codes
            // above all) must be stable, and "whichever row came back first" is not stable.
            if (entity.IsPrimary)
            {
                List<CompanyDomain> others = await ctx.CompanyDomains
                    .Where(d => d.CompanyId == entity.CompanyId && d.Id != entity.Id && d.IsPrimary)
                    .ToListAsync();

                foreach (CompanyDomain other in others)
                {
                    other.IsPrimary = false;
                    other.UpdateDate = now;
                }
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Saved domain {Hostname} for company {CompanyId} (primary: {IsPrimary})",
                entity.Hostname, entity.CompanyId, entity.IsPrimary);

            return Result<CompanyDomain>.Ok(entity);
        }
        catch (Exception ex)
        {
            return Result<CompanyDomain>.Fail($"Failed to save company domain: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateDomainAsync(int domainId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            CompanyDomain? domain = await ctx.CompanyDomains.FirstOrDefaultAsync(d => d.Id == domainId);

            if (domain is null)
            {
                return Result<bool>.Fail("Domain not found.");
            }

            domain.IsActive = false;
            domain.UpdateDate = DateTime.UtcNow;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Deactivated domain {Hostname} for company {CompanyId}", domain.Hostname, domain.CompanyId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to deactivate company domain: {ex.Message}");
        }
    }

    private static async Task<TenantBrandingDto> BuildBrandingAsync(ApplicationDbContext ctx, Company company)
    {
        string? primaryHost = await ctx.CompanyDomains
            .AsNoTracking()
            .TagWithCallSite()
            .Where(d => d.CompanyId == company.Id && d.IsActive && d.IsPrimary)
            .Select(d => d.Hostname)
            .FirstOrDefaultAsync();

        return new TenantBrandingDto
        {
            CompanyId = company.Id,
            CompanyName = company.Name,
            PrimaryHost = primaryHost,
            ThemeColorHex = BrandConstants.ResolveAccentColor(company.ThemeColorHex),
            SecondaryColorHex = BrandConstants.ResolveSecondaryColor(company.SecondaryColorHex, company.ThemeColorHex),
            OnPrimaryColorHex = BrandConstants.ResolveOnPrimaryColor(company.ThemeColorHex),
            HasLogo = company.LogoFileId is not null
        };
    }

    // Trims a leading "www." only when matching would otherwise fail? No - deliberately not.
    // www and apex are separate rows so a customer can point one at us before the other; folding
    // them here would silently resolve a host nobody configured.
    private static string NormalizeHostname(string hostname)
    {
        string trimmed = hostname.Trim().ToLowerInvariant();

        // Cloudflare forwards the host with a port in some configurations; the table stores none.
        int colonIndex = trimmed.IndexOf(':');

        return colonIndex >= 0 ? trimmed[..colonIndex] : trimmed;
    }
}
