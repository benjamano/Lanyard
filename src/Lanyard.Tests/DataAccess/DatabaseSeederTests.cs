using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

        // EF InMemory has no foreign keys, so it can't reproduce the real Postgres 23503 error
        // (AspNetRoles.CreatedByUserId FK to AspNetUsers) on its own. This interceptor simulates
        // that constraint by failing a SaveChanges that would insert an ApplicationRole whose
        // CreatedByUserId doesn't reference a User row that a *previous* SaveChanges already
        // persisted - which is exactly what a real FK check does when the referenced row hasn't
        // been inserted yet, even within the same transaction.
        private sealed class CreatedByUserIdForeignKeySimulator : SaveChangesInterceptor
        {
            public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
            {
                Validate(eventData.Context);
                return base.SavingChanges(eventData, result);
            }

            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                Validate(eventData.Context);
                return base.SavingChangesAsync(eventData, result, cancellationToken);
            }

            private static void Validate(DbContext? context)
            {
                if (context is null)
                {
                    return;
                }

                foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<ApplicationRole> entry in context.ChangeTracker.Entries<ApplicationRole>())
                {
                    if (entry.State != EntityState.Added)
                    {
                        continue;
                    }

                    string createdByUserId = entry.Entity.CreatedByUserId;
                    bool referencedUserAlreadyPersisted = context.Set<UserProfile>().Any(u => u.Id == createdByUserId);

                    if (!referencedUserAlreadyPersisted)
                    {
                        throw new InvalidOperationException(
                            $"Simulated FK violation: AspNetRoles.CreatedByUserId '{createdByUserId}' does not "
                            + "reference an AspNetUsers row that has been saved yet.");
                    }
                }
            }
        }

        private static DbContextOptions<ApplicationDbContext> GetInMemoryOptionsWithCreatedByUserIdFkSimulation()
        {
            return new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .AddInterceptors(new CreatedByUserIdForeignKeySimulator())
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
        public async Task SeedAsync_OnGenuinelyEmptyDatabase_CreatesAdminUserBeforeRolesReferenceIt()
        {
            DbContextOptions<ApplicationDbContext> options = GetInMemoryOptionsWithCreatedByUserIdFkSimulation();
            using ServiceProvider provider = BuildServiceProvider(options, "Configured-Admin-Pw1!", "Production");

            // Would throw the simulated FK violation above if standard roles were seeded before
            // the admin user they're stamped with as CreatedByUserId - reproducing the real
            // Postgres 23503 error on AspNetRoles.CreatedByUserId against an empty database.
            await DatabaseSeeder.SeedAsync(provider);

            await using ApplicationDbContext context = new(options);
            bool adminExists = await context.Users.AnyAsync(u => u.Id == ApplicationDbContext.SeedAdminUserId);
            int roleCount = await context.Roles.CountAsync();
            int adminRoleAssignmentCount = await context.UserRoles.CountAsync(ur => ur.UserId == ApplicationDbContext.SeedAdminUserId);

            Assert.IsTrue(adminExists);
            Assert.AreEqual(8, roleCount);
            Assert.AreEqual(5, adminRoleAssignmentCount);
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
            Assert.AreEqual(8, roleCount, "Roles must not be duplicated when the admin is recreated.");
        }
    }
}
