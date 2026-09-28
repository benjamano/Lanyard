using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lanyard.Application.Services.Tenancy;

public static class TenancyServiceCollectionExtensions
{
    // Call after AddDbContextFactory<ApplicationDbContext>. That registers the options plus a
    // singleton factory; this swaps the factory for a scoped one that stamps each context with the
    // caller's company, so every component and scoped service is filtered to its own company
    // without asking. Singletons and hosted services can't take a scoped dependency, so they use
    // ISystemDbContextFactory and must scope their own queries (see TenantContext for who counts
    // as "system").
    public static IServiceCollection AddCompanyTenancy(this IServiceCollection services)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantProvider>(sp => sp.GetRequiredService<TenantContext>());

        services.Replace(ServiceDescriptor.Scoped<IDbContextFactory<ApplicationDbContext>, TenantDbContextFactory>());
        services.Replace(ServiceDescriptor.Scoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext()));
        services.Replace(ServiceDescriptor.Singleton<ISystemDbContextFactory, SystemDbContextFactory>());

        return services;
    }
}
