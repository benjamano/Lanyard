using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class DatabaseSeeder
{
    private const string DevelopmentAdminPassword = "Dev-Admin-Pw1!";

    private static readonly (string Id, string Name, string ConcurrencyStamp)[] StandardRoles =
    [
        (ApplicationDbContext.SeedAdminRoleId, "Admin", "SEED-ROLE-ADMIN-CS"),
        (ApplicationDbContext.SeedManagerRoleId, "Manager", "SEED-ROLE-MANAGER-CS"),
        (ApplicationDbContext.SeedStaffRoleId, "Staff", "SEED-ROLE-STAFF-CS"),
        (ApplicationDbContext.SeedCanControlMusicRoleId, "CanControlMusic", "SEED-ROLE-CAN-CONTROL-MUSIC-CS"),
        (ApplicationDbContext.SeedCanClockInRoleId, "CanClockIn", "SEED-ROLE-CAN-CLOCK-IN-CS"),
        (ApplicationDbContext.SeedCanManageDmxSystemsRoleId, "CanManageDmxSystems", "SEED-ROLE-CAN-MANAGE-DMX-SYSTEMS-CS"),
        (ApplicationDbContext.SeedCanManageFilesRoleId, "CanManageFiles", "SEED-ROLE-CAN-MANAGE-FILES-CS"),
        (ApplicationDbContext.SeedCanManageKitchenRoleId, "CanManageKitchen", "SEED-ROLE-CAN-MANAGE-KITCHEN-CS"),
        (ApplicationDbContext.SeedCanPostAnnouncementsRoleId, "CanPostAnnouncements", "SEED-ROLE-CAN-POST-ANNOUNCEMENTS-CS"),
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
        IConfiguration configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        IHostEnvironment environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        ILogger logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSeeder");

        await SeedAdminUserAsync(context, userManager, configuration, environment, logger);

        await SeedGdprPlaceholderUserAsync(context, userManager);

        await SeedCompanyAndLocationsAsync(context);
        await SeedCompanyDomainsAsync(context);

        await ResetIdentitySequencesAsync(context);
    }

    private static async Task SeedAdminUserAsync(ApplicationDbContext context, UserManager<UserProfile> userManager, IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        bool adminExists = await context.Users.AnyAsync(u => u.Id == ApplicationDbContext.SeedAdminUserId);

        // Creating the admin user, backfilling standard roles, and assigning those roles to the
        // admin are a genuine circular dependency, not just an ordering nicety: AspNetRoles.
        // CreatedByUserId (NOT NULL, FK to AspNetUsers) requires the admin user row to exist
        // first, while the admin's AspNetUserRoles rows require the Role rows to exist first.
        // Postgres resolves FK checks against rows already inserted earlier in the same
        // transaction even before commit, so doing all three inside one transaction - in the
        // order below - satisfies both constraints on a genuinely empty database. Wrapping
        // everything together also preserves the original guarantee that a failure partway
        // through cannot leave a permanently role-less admin - the "already exists"/"already
        // assigned" guards here would otherwise skip re-seeding it on every later startup.
        // Guarded by IsNpgsql() like ResetIdentitySequencesAsync below - the EF InMemory provider
        // used by tests doesn't support transactions at all.
        await using IDbContextTransaction? transaction = context.Database.IsNpgsql()
            ? await context.Database.BeginTransactionAsync()
            : null;

        if (!adminExists)
        {
            UserProfile seedAdminUser = new UserProfile
            {
                Id = ApplicationDbContext.SeedAdminUserId,
                UserName = "admin",
                Email = "admin@play2day.com",
                EmailConfirmed = true,
                FirstName = "System",
                LastName = "Administrator",
                // Left null so the admin row is visibly a first-run credential.
                PasswordSetDate = null,
                // NormalizedUserName/NormalizedEmail/SecurityStamp are deliberately not set here -
                // UserManager.CreateAsync(user, password) always recomputes the first two via the
                // configured ILookupNormalizer and always overwrites SecurityStamp with a fresh
                // random value, so any value assigned here would be silently discarded.
                ConcurrencyStamp = "SEED-ADMIN-CONCURRENCY-STAMP"
            };
            string? configuredPassword = configuration["Seed:AdminPassword"];
            string adminPassword;

            if (!string.IsNullOrWhiteSpace(configuredPassword))
            {
                adminPassword = configuredPassword;

                logger.LogInformation("Seeding admin user with the password supplied via Seed:AdminPassword.");
            }
            else if (environment.IsDevelopment())
            {
                adminPassword = DevelopmentAdminPassword;

                logger.LogWarning(
                    "No Seed:AdminPassword configured. Seeding admin user '{UserName}' with the well-known "
                    + "development password. Set Seed__AdminPassword before deploying outside Development.",
                    seedAdminUser.UserName);
            }
            else
            {
                throw new InvalidOperationException(
                    "Seed:AdminPassword is not configured. Set the Seed__AdminPassword environment variable "
                    + "before starting the application outside the Development environment.");
            }

            IdentityResult createResult = await userManager.CreateAsync(seedAdminUser, adminPassword);

            if (!createResult.Succeeded)
            {
                string errors = string.Join(", ", createResult.Errors.Select(e => e.Description));

                throw new InvalidOperationException($"Failed to seed admin user: {errors}");
            }
        }

        // Unconditional, like ResetIdentitySequencesAsync below - a role added to StandardRoles
        // after go-live must still backfill even if the fixed-ID seed admin account has since
        // been deleted (a normal thing to do once real admins exist).
        await EnsureStandardRolesExistAsync(context);

        // Guarded rather than folded into the `!adminExists` branch above: once the admin exists,
        // this must still run and be idempotent, since a pre-existing admin (recreated after
        // deletion, or one whose roles previously failed to save) can no longer be assumed to
        // have its role assignments already made.
        if (!await context.UserRoles.AnyAsync(ur => ur.UserId == ApplicationDbContext.SeedAdminUserId))
        {
            await context.UserRoles.AddRangeAsync(
                new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedAdminRoleId },
                new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedManagerRoleId },
                new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedStaffRoleId },
                new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedCanControlMusicRoleId },
                new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedCanClockInRoleId }
            );

            await context.SaveChangesAsync();
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync();
        }
    }

    // Reserved account that non-nullable attribution FKs (DmxScene/DmxSceneStep/
    // DmxSceneStepChannelValue.CreateByUserId, ApplicationRole.CreatedByUserId) are repointed to
    // by GdprService when the real author's account is erased - those fields are `required string`,
    // so nulling them out is not an option. Never assigned a password, permanently locked out, and
    // excluded from SecurityService.GetActiveUsersAsync so it can never be used or shown as a real user.
    private static async Task SeedGdprPlaceholderUserAsync(ApplicationDbContext context, UserManager<UserProfile> userManager)
    {
        if (await context.Users.AnyAsync(u => u.Id == ApplicationDbContext.SystemDeletedUserPlaceholderId))
        {
            return;
        }

        UserProfile placeholder = new()
        {
            Id = ApplicationDbContext.SystemDeletedUserPlaceholderId,
            UserName = "deleted-user",
            Email = null,
            EmailConfirmed = false,
            FirstName = "Deleted",
            LastName = "User",
            PasswordSetDate = null,
            LockoutEnabled = true,
            LockoutEnd = DateTimeOffset.MaxValue,
            ConcurrencyStamp = "SEED-GDPR-PLACEHOLDER-CONCURRENCY-STAMP"
        };

        // CreateAsync (no password overload) leaves PasswordHash null - there is no credential
        // that could ever authenticate as this account, on top of the permanent lockout above.
        IdentityResult result = await userManager.CreateAsync(placeholder);

        if (!result.Succeeded)
        {
            string errors = string.Join(", ", result.Errors.Select(e => e.Description));

            throw new InvalidOperationException($"Failed to seed GDPR placeholder user: {errors}");
        }
    }

    private static async Task EnsureStandardRolesExistAsync(ApplicationDbContext context)
    {
        // Compared by Id, not NormalizedName - every StandardRoles entry has a fixed, known Id, and
        // an Id-based check stays correct even if a deployment renames a seeded role afterwards
        // (a name-based check would then find no match and try to re-insert the same Id, throwing
        // a primary-key violation on every startup from then on).
        HashSet<string> existingIds = (await context.Roles
            .AsNoTracking()
            .TagWithCallSite()
            .Select(r => r.Id)
            .ToListAsync())
            .ToHashSet();

        List<ApplicationRole> missingRoles = StandardRoles
            .Where(r => !existingIds.Contains(r.Id))
            .Select(ToApplicationRole)
            .ToList();

        if (missingRoles.Count == 0)
        {
            return;
        }

        await context.Roles.AddRangeAsync(missingRoles);
        await context.SaveChangesAsync();
    }

    private static ApplicationRole ToApplicationRole((string Id, string Name, string ConcurrencyStamp) role)
    {
        return new ApplicationRole
        {
            Id = role.Id,
            Name = role.Name,
            NormalizedName = role.Name.ToUpperInvariant(),
            ConcurrencyStamp = role.ConcurrencyStamp,
            CreatedByUserId = ApplicationDbContext.SeedAdminUserId,
            CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
            IsActive = true
        };
    }

    private static async Task SeedCompanyAndLocationsAsync(ApplicationDbContext context)
    {
        if (await context.Companies.AnyAsync(c => c.Id == ApplicationDbContext.SeedPlay2DayCompanyId))
        {
            return;
        }

        Company play2Day = new()
        {
            Id = ApplicationDbContext.SeedPlay2DayCompanyId,
            Name = "Play2Day",
            Slug = "play2day",
            IsActive = true,
            CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
            UpdateDate = ApplicationDbContext.SeedRoleCreateDateUtc
        };

        await context.Companies.AddAsync(play2Day);

        await context.Locations.AddRangeAsync(
            new Location
            {
                Id = ApplicationDbContext.SeedIpswichLocationId,
                CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId,
                Name = "Ipswich",
                IsActive = true,
                CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
                UpdateDate = ApplicationDbContext.SeedRoleCreateDateUtc
            },
            new Location
            {
                Id = ApplicationDbContext.SeedWisbechLocationId,
                CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId,
                Name = "Wisbech",
                IsActive = true,
                CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
                UpdateDate = ApplicationDbContext.SeedRoleCreateDateUtc
            });

        await context.UserLocationMemberships.AddRangeAsync(
            new UserLocationMembership { UserId = ApplicationDbContext.SeedAdminUserId, LocationId = ApplicationDbContext.SeedIpswichLocationId, CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc },
            new UserLocationMembership { UserId = ApplicationDbContext.SeedAdminUserId, LocationId = ApplicationDbContext.SeedWisbechLocationId, CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc });

        await context.SaveChangesAsync();
    }

    // Deliberately separate from SeedCompanyAndLocationsAsync, which returns early once the
    // seeded company exists: a developer whose database predates multi-tenancy still needs a
    // hostname mapping, or the public site has no tenant to resolve and serves nothing but 404s.
    private static async Task SeedCompanyDomainsAsync(ApplicationDbContext context)
    {
        if (await context.CompanyDomains.AnyAsync(d => d.CompanyId == ApplicationDbContext.SeedPlay2DayCompanyId))
        {
            return;
        }

        if (!await context.Companies.AnyAsync(c => c.Id == ApplicationDbContext.SeedPlay2DayCompanyId))
        {
            return;
        }

        // localhost only - a real customer domain is onboarded through the admin UI, never
        // hardcoded here, since that is the whole point of resolving tenants from a table.
        await context.CompanyDomains.AddAsync(new CompanyDomain
        {
            CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId,
            Hostname = "localhost",
            IsPrimary = true,
            IsActive = true,
            CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
            UpdateDate = ApplicationDbContext.SeedRoleCreateDateUtc
        });

        await context.SaveChangesAsync();
    }

    private static async Task ResetIdentitySequencesAsync(ApplicationDbContext context)
    {
        if (!context.Database.IsNpgsql())
        {
            return;
        }

        await context.Database.ExecuteSqlRawAsync(
            "SELECT setval(pg_get_serial_sequence('\"Companies\"', 'Id'), (SELECT COALESCE(MAX(\"Id\"), 1) FROM \"Companies\"));");
        await context.Database.ExecuteSqlRawAsync(
            "SELECT setval(pg_get_serial_sequence('\"Locations\"', 'Id'), (SELECT COALESCE(MAX(\"Id\"), 1) FROM \"Locations\"));");
    }
}