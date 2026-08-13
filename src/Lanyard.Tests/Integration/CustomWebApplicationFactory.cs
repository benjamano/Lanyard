using Lanyard.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lanyard.Tests.Integration;

// Boots the real app pipeline (auth middleware, routing, RouteAuthorizationGate, controllers)
// against an isolated in-memory database instead of the real Postgres Program.cs otherwise
// requires. One instance = one isolated database, so create a fresh instance per test rather
// than sharing one across tests.
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString();

    // Program.cs's own AddDbContextFactory(...UseNpgsql...) call already registered Npgsql's
    // internal EF services into the app's shared IServiceCollection. Removing the
    // DbContextOptions<T>/IDbContextFactory<T>/T registrations below doesn't remove those - EF
    // then finds both Npgsql's and InMemory's internal services in the same container and throws
    // "Only a single database provider can be registered". Giving InMemory its own isolated
    // internal service provider sidesteps the collision entirely instead of trying to surgically
    // un-register Npgsql's services.
    private readonly IServiceProvider _inMemoryServiceProvider = new ServiceCollection()
        .AddEntityFrameworkInMemoryDatabase()
        .BuildServiceProvider();

    public const string SeedAdminPassword = "Integration-Test-Admin-Pw1!";

    public CustomWebApplicationFactory()
    {
        // OpenTelemetry's UseOtlpExporter() reads OTEL_EXPORTER_OTLP_ENDPOINT via a raw
        // Environment.GetEnvironmentVariable call (an OTel SDK convention, not routed through
        // IConfiguration/UseSetting), so it picks up whatever's set on the machine running the
        // tests - e.g. a developer's real Seq server. Overriding it here keeps the test host
        // hermetic regardless of the local machine's environment; port 1 fails the connection
        // immediately rather than actually reaching anything.
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:1");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Development" avoids two things that would otherwise break this host: the
        // HTTPS-redirection/HSTS/forwarded-headers middleware Program.cs only adds outside
        // Development (this test client talks plain HTTP), and the explicit
        // Database.MigrateAsync() call Program.cs only makes outside Development - relational
        // migrations aren't a concept the EF InMemory provider supports and would throw.
        builder.UseEnvironment("Development");

        builder.UseSetting("Seed:AdminPassword", SeedAdminPassword);

        // Program.cs throws at startup if this is empty (see the guard clause right before
        // AddDbContextFactory). The value itself is never actually connected to - the
        // Npgsql-configured registration this satisfies gets removed and replaced below.
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=unused;Database=unused;Username=unused;Password=unused");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextFactory<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();

            // AddDbContextFactory also registers ApplicationDbContext itself as directly
            // injectable (Identity's UserStore/RoleStore and DatabaseSeeder both take a
            // directly-injected ApplicationDbContext, not the factory) - one call here keeps
            // both resolution paths pointed at the same named in-memory database.
            services.AddDbContextFactory<ApplicationDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName);
                options.UseInternalServiceProvider(_inMemoryServiceProvider);
            });
        });
    }
}
