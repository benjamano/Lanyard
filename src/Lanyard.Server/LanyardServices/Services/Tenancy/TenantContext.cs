using System.Collections.Concurrent;
using System.Security.Claims;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lanyard.Application.Services.Tenancy;

// Works out which company the current scope is acting for, for ApplicationDbContext's tenant
// filter. Scoped: one per Blazor circuit or HTTP request, and a circuit's user never changes
// (logging in or out is a full page load), so the answer is worked out once and kept.
//
//  - Signed-in user    -> their session's company (the company claim, or the company of the
//                         location claim for sessions issued before the company claim existed).
//                         If neither resolves they see nothing: fail closed.
//  - No signed-in user -> system (unfiltered). That's kiosks, anonymous pages, SignalR from
//                         kiosk clients, hosted services and startup work - the same access they
//                         had before tenancy, and none of them act for a demo visitor.
public interface ITenantContext : ITenantProvider
{
    // Lanyard operators, who may manage every company (see LanyardRoles.PlatformAdmin). Their data
    // queries are still filtered to the company they signed in to - this only widens the
    // company/location administration surface.
    bool IsPlatformAdmin { get; }

    // The one company a caller is limited to when managing companies, locations and memberships,
    // or null when they may manage any (system callers and platform admins).
    int? ManageableCompanyId { get; }

    bool CanManageCompany(int companyId);
}

public sealed class TenantContext(
    IServiceProvider services,
    IHttpContextAccessor httpContextAccessor,
    DbContextOptions<ApplicationDbContext> dbOptions) : ITenantContext
{
    // Locations never move between companies, so this is safe to share for the process lifetime.
    private static readonly ConcurrentDictionary<int, int?> CompanyIdByLocationId = new();

    private bool _resolved;
    private bool _isSystem;
    private bool _isPlatformAdmin;
    private int? _companyId;

    public bool IsPlatformAdmin
    {
        get
        {
            EnsureResolved();
            return _isPlatformAdmin;
        }
    }

    // -1 for a signed-in user with no resolvable company: they can manage nothing.
    public int? ManageableCompanyId => IsSystem || IsPlatformAdmin ? null : CompanyId ?? -1;

    public bool CanManageCompany(int companyId) => ManageableCompanyId is null || ManageableCompanyId == companyId;

    public bool IsSystem
    {
        get
        {
            EnsureResolved();
            return _isSystem;
        }
    }

    public int? CompanyId
    {
        get
        {
            EnsureResolved();
            return _companyId;
        }
    }

    private void EnsureResolved()
    {
        if (_resolved)
        {
            return;
        }

        ClaimsPrincipal? user = GetCircuitUser() ?? httpContextAccessor.HttpContext?.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            _isSystem = true;
            _companyId = null;
        }
        else
        {
            _isSystem = false;
            _isPlatformAdmin = user.IsInRole(LanyardRoles.PlatformAdmin);
            _companyId = ResolveCompanyId(user);
        }

        _resolved = true;
    }

    private ClaimsPrincipal? GetCircuitUser()
    {
        AuthenticationStateProvider? provider = services.GetService<AuthenticationStateProvider>();

        if (provider is null)
        {
            return null;
        }

        try
        {
            // Inside a circuit (or a component render) the state is already set, so this task is
            // complete and GetResult doesn't block. Outside one - controllers, hosted services -
            // the server provider throws because nothing has set it yet, and we fall back.
            Task<AuthenticationState> stateTask = provider.GetAuthenticationStateAsync();

            return stateTask.IsCompletedSuccessfully ? stateTask.Result.User : stateTask.GetAwaiter().GetResult().User;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private int? ResolveCompanyId(ClaimsPrincipal user)
    {
        if (int.TryParse(user.FindFirstValue(LocationClaimTypes.CompanyId), out int companyId))
        {
            return companyId;
        }

        if (!int.TryParse(user.FindFirstValue(LocationClaimTypes.LocationId), out int locationId))
        {
            return null;
        }

        return CompanyIdByLocationId.GetOrAdd(locationId, id =>
        {
            // A plain context (no tenant) - Locations aren't company-filtered anyway, and going
            // through the tenant-aware factory here would recurse back into this resolver.
            using ApplicationDbContext ctx = new(dbOptions);

            return ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == id)
                .Select(x => (int?)x.CompanyId)
                .FirstOrDefault();
        });
    }
}
