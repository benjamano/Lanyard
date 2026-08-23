using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.DataAccess
{
    [TestClass]
    public class DatabaseSeederTests
    {
        private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
        {
            return new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        }

        // Mirrors SecurityServiceTests.BuildUserManager: DataProtectionTokenProvider<TUser> is
        // internal to Identity, so a real DI container is built to get a UserManager wired the
        // same way Program.cs wires one, rather than trying to mock around it.
        private static ServiceProvider BuildServiceProvider(
            DbContextOptions<ApplicationDbContext> options,
            string? seedAdminPassword,
            string environmentName)
        {
            ServiceCollection services = new();

            services.AddSingleton(options);
            services.AddScoped(sp => new ApplicationDbContext(sp.GetRequiredService<DbContextOptions<ApplicationDbContext>>()));
            services.AddDataProtection();
            services.AddLogging();

            services.AddIdentityCore<UserProfile>(o => o.Password.RequireNonAlphanumeric = false)
                .AddRoles<ApplicationRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            Dictionary<string, string?> configValues = [];

            if (seedAdminPassword is not null)
            {
                configValues["Seed:AdminPassword"] = seedAdminPassword;
            }

            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
            services.AddSingleton(configuration);

            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));

            return services.BuildServiceProvider();
        }

        private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = environmentName;
            public string ApplicationName { get; set; } = "Lanyard.Tests";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        }

        [TestMethod]
        public async Task SeedAsync_WhenPasswordConfigured_CreatesAdminUserWithoutPasswordSetDate()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            using ServiceProvider provider = BuildServiceProvider(options, "Configured-Admin-Pw1!", "Production");

            await DatabaseSeeder.SeedAsync(provider);

            await using ApplicationDbContext context = new(options);
            UserProfile? admin = await context.Users.SingleOrDefaultAsync(u => u.Id == ApplicationDbContext.SeedAdminUserId);

            Assert.IsNotNull(admin);
            Assert.IsNotNull(admin.PasswordHash);
            Assert.IsNull(admin.PasswordSetDate);
        }

        [TestMethod]
        public async Task SeedAsync_WhenPasswordNotConfiguredInDevelopment_SeedsWithDevelopmentPassword()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            using ServiceProvider provider = BuildServiceProvider(options, seedAdminPassword: null, "Development");

            await DatabaseSeeder.SeedAsync(provider);

            await using ApplicationDbContext context = new(options);
            bool adminExists = await context.Users.AnyAsync(u => u.Id == ApplicationDbContext.SeedAdminUserId);

            Assert.IsTrue(adminExists);
        }

        [TestMethod]
        public async Task SeedAsync_WhenPasswordNotConfiguredOutsideDevelopment_Throws()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
            using ServiceProvider provider = BuildServiceProvider(options, seedAdminPassword: null, "Production");

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => DatabaseSeeder.SeedAsync(provider));

            await using ApplicationDbContext context = new(options);
            bool adminExists = await context.Users.AnyAsync(u => u.Id == ApplicationDbContext.SeedAdminUserId);

            Assert.IsFalse(adminExists, "A failed seed attempt must not leave a partially-created admin user.");
        }

        [TestMethod]
        public async Task SeedAsync_WhenAdminAlreadyExists_IsNoOpAndDoesNotThrowOutsideDevelopment()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

            await using (ApplicationDbContext seedContext = new(options))
            {
                seedContext.Users.Add(new UserProfile
                {
                    Id = ApplicationDbContext.SeedAdminUserId,
                    UserName = "admin",
                    NormalizedUserName = "ADMIN",
                    Email = "admin@play2day.com",
                    NormalizedEmail = "ADMIN@PLAY2DAY.COM",
                    SecurityStamp = "EXISTING-ADMIN-SECURITY-STAMP",
                    ConcurrencyStamp = "EXISTING-ADMIN-CONCURRENCY-STAMP"
                });

                await seedContext.SaveChangesAsync();
            }

            // No Seed:AdminPassword configured and not Development - would throw if the
            // environment check ran, but the early "admin already exists" return must happen first.
            using ServiceProvider provider = BuildServiceProvider(options, seedAdminPassword: null, "Production");

            await DatabaseSeeder.SeedAsync(provider);
        }

        [TestMethod]
        public async Task SeedAsync_WhenSeedAdminWasDeletedButRolesRemain_RecreatesAdminWithoutDuplicatingRoles()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

            // First run: seeds the admin user and all standard roles normally.
            using (ServiceProvider firstRunProvider = BuildServiceProvider(options, "Configured-Admin-Pw1!", "Production"))
            {
                await DatabaseSeeder.SeedAsync(firstRunProvider);
            }

            // Simulate an operator deleting the seed admin account after onboarding real admins -
            // a normal thing to do once real admins exist. Real Postgres cascade-deletes
            // AspNetUserRoles via Identity's default FK config (ON DELETE CASCADE), regardless of
            // what's tracked; the EF InMemory provider only cascades entities it has loaded, so
            // the UserRoles rows are removed explicitly here to reproduce that real behavior. The
            // shared Role rows are untouched either way.
            await using (ApplicationDbContext context = new(options))
            {
                UserProfile admin = await context.Users.SingleAsync(u => u.Id == ApplicationDbContext.SeedAdminUserId);
                List<IdentityUserRole<string>> adminRoleAssignments = await context.UserRoles
                    .Where(ur => ur.UserId == ApplicationDbContext.SeedAdminUserId)
                    .ToListAsync();

                context.UserRoles.RemoveRange(adminRoleAssignments);
                context.Users.Remove(admin);
                await context.SaveChangesAsync();
            }

            using ServiceProvider secondRunProvider = BuildServiceProvider(options, "Configured-Admin-Pw1!", "Production");

            // Must not throw trying to re-insert roles that already exist.
            await DatabaseSeeder.SeedAsync(secondRunProvider);

            await using ApplicationDbContext verifyContext = new(options);
            bool adminRecreated = await verifyContext.Users.AnyAsync(u => u.Id == ApplicationDbContext.SeedAdminUserId);
            int roleCount = await verifyContext.Roles.CountAsync();

            Assert.IsTrue(adminRecreated, "The seed admin should be recreated on the next startup.");
            Assert.AreEqual(7, roleCount, "Roles must not be duplicated when the admin is recreated.");
        }
    }
}
