using Lanyard.Infrastructure.DataAccess;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// Program.cs fails fast at boot outside Development when Clients:SharedSecret is unset, rather
// than silently serving the kiosk file/audio endpoints and the /websocket hub with no
// authentication. This is the one piece of that behaviour a unit test can't reach - it lives in
// Program.cs's top-level statements, before any service is constructed - so it needs a real host
// boot via WebApplicationFactory<Program>, same as CustomWebApplicationFactory but pointed at a
// non-Development environment with no shared secret configured.
[TestClass]
public class SharedSecretStartupIntegrationTests
{
    // Mirrors CustomWebApplicationFactory's InMemory-provider swap. The throw under test happens
    // in Program.cs before Build() is called, i.e. before this override even runs - but the
    // override still has to be registered so that IF the throw doesn't fire (test failure case)
    // the host does not also fail for the unrelated reason of colliding EF providers.
    private sealed class UnconfiguredSecretFactory : WebApplicationFactory<Program>
    {
        private readonly IServiceProvider _inMemoryServiceProvider = new ServiceCollection()
            .AddEntityFrameworkInMemoryDatabase()
            .BuildServiceProvider();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=unused;Database=unused;Username=unused;Password=unused");

            // Deliberately not setting Clients:SharedSecret - appsettings.json's checked-in
            // default is an empty string, so this reproduces the unconfigured case.

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<IDbContextFactory<ApplicationDbContext>>();
                services.RemoveAll<ApplicationDbContext>();

                services.AddDbContextFactory<ApplicationDbContext>(options =>
                {
                    options.UseInMemoryDatabase(Guid.NewGuid().ToString());
                    options.UseInternalServiceProvider(_inMemoryServiceProvider);
                });
            });
        }
    }

    [TestMethod]
    public void UnsetSharedSecret_OutsideDevelopment_FailsAtStartup()
    {
        using UnconfiguredSecretFactory factory = new();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            // The host is actually built lazily the first time it's touched.
            _ = factory.Services;
        });
    }
}
