using Microsoft.EntityFrameworkCore;

namespace Lanyard.Infrastructure.DataAccess.Tenancy;

// The IDbContextFactory<ApplicationDbContext> that components and scoped services get: every
// context it creates is filtered to the current caller's company.
public sealed class TenantDbContextFactory(DbContextOptions<ApplicationDbContext> options, ITenantProvider tenant)
    : IDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext() => new(options, tenant);
}

// For singletons and hosted services, which outlive any one user and so can't take the scoped
// tenant-aware factory. Contexts from here are unfiltered: code using this must scope its own
// queries to the right company (usually via the kiosk Client or rule it's acting on).
public interface ISystemDbContextFactory : IDbContextFactory<ApplicationDbContext>;

public sealed class SystemDbContextFactory(DbContextOptions<ApplicationDbContext> options) : ISystemDbContextFactory
{
    public ApplicationDbContext CreateDbContext() => new(options);
}
