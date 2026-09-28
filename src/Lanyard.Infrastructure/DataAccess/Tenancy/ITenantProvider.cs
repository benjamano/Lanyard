namespace Lanyard.Infrastructure.DataAccess.Tenancy;

// Tells ApplicationDbContext which company's rows the current unit of work may see and write.
// Implemented in the server (TenantContext); a context created without one behaves as "system"
// (unfiltered), which is what tests and design-time tooling rely on.
public interface ITenantProvider
{
    // True when company filtering must be skipped entirely: background work, kiosks and anonymous
    // pages. A signed-in user is never a system caller.
    bool IsSystem { get; }

    // The company the caller belongs to. Null when IsSystem is true. When IsSystem is false and
    // this is null, the caller couldn't be tied to a company and must see nothing (fail closed).
    int? CompanyId { get; }
}

// For when someone runs as a specific company outside a signed-in user's context,
// e.g. a hosted sweep acting on each company in turn, or the demo seeder.
public sealed class FixedTenantProvider(int? companyId) : ITenantProvider
{
    public static readonly FixedTenantProvider System = new(null);

    public bool IsSystem => companyId is null;
    public int? CompanyId => companyId;
}
