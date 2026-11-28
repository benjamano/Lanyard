using Microsoft.Extensions.Caching.Memory;

namespace Lanyard.Application.Services.Locations;

// Cache keys and eviction for the per-company and per-user lookups every page makes: company
// branding (layout, home, install links, logo endpoints), a location's scope details, which company
// a user belongs to, and a user's seasonal-theme opt-in.
//
// One shared IMemoryCache, so every key carries what kind of id it holds - a company id and a
// location id with the same number can never collide, and nothing is ever keyed by anything but the
// row it describes, so one company's branding can't be served under another's id.
//
// Writes that go through the services evict straight away. The TTL bounds staleness from writes
// that don't (demo reset, a membership changed by another service), and from a second instance.
public static class BrandingCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    // Memberships change in several services (staff bulk updates, GDPR erasure, the demo reset), so
    // this one stays short rather than relying on every one of them to evict.
    public static readonly TimeSpan UserCompanyTtl = TimeSpan.FromMinutes(1);

    public static string Branding(int companyId) => $"company-branding:{companyId}";

    public static string UserCompany(string userId) => $"user-company:{userId}";

    public static string LocationScope(int locationId) => $"location-scope:{locationId}";

    public static string SeasonalOptIn(string userId) => $"seasonal-opt-in:{userId}";

    public static string ImageContentType(Guid fileId) => $"branding-file-type:{fileId:N}";

    // A company's branding and its locations' scope details, which carry the company name.
    public static void EvictCompany(IMemoryCache cache, int companyId, IEnumerable<int> locationIds)
    {
        cache.Remove(Branding(companyId));

        foreach (int locationId in locationIds)
        {
            cache.Remove(LocationScope(locationId));
        }
    }
}
