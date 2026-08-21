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
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
        IConfiguration configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        IHostEnvironment environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        ILogger logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSeeder");

        // Roles are seeded first and unconditionally, like ResetIdentitySequencesAsync below - a
        // role added to StandardRoles after go-live must still backfill even if the fixed-ID seed
        // admin account has since been deleted (a normal thing to do once real admins exist). This
        // also has to run before the admin user: assigning the admin's UserRoles further down
        // depends on the Role rows already existing, since IdentityUserRole has real FK constraints.
        await EnsureStandardRolesExistAsync(context);

        await SeedAdminUserAsync(context, userManager, configuration, environment, logger);

        await SeedCompanyAndLocationsAsync(context);

        await ResetIdentitySequencesAsync(context);
    }

    private static async Task SeedAdminUserAsync(ApplicationDbContext context, UserManager<UserProfile> userManager, IConfiguration configuration, IHostEnvironment environment, ILogger logger)
    {
        if (await context.Users.AnyAsync(u => u.Id == ApplicationDbContext.SeedAdminUserId))
        {
            return;
        }

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

        // UserManager.CreateAsync commits immediately (UserStore.AutoSaveChanges defaults to
        // true), independent of the UserRoles SaveChangesAsync below. Without an explicit ambient
        // transaction, a failure between the two would permanently persist an admin user with zero
        // role assignments - the "already exists" guard above would then skip re-seeding forever.
        // Guarded by IsNpgsql() like ResetIdentitySequencesAsync below - the EF InMemory provider
        // used by tests doesn't support transactions at all.
        await using IDbContextTransaction? transaction = context.Database.IsNpgsql()
            ? await context.Database.BeginTransactionAsync()
            : null;

        IdentityResult createResult = await userManager.CreateAsync(seedAdminUser, adminPassword);

        if (!createResult.Succeeded)
        {
            string errors = string.Join(", ", createResult.Errors.Select(e => e.Description));

            throw new InvalidOperationException($"Failed to seed admin user: {errors}");
        }

        await context.UserRoles.AddRangeAsync(
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedAdminRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedManagerRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedStaffRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedCanControlMusicRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedCanClockInRoleId }
        );

        await context.SaveChangesAsync();

        if (transaction is not null)
        {
            await transaction.CommitAsync();
        }
    }

    private static async Task EnsureStandardRolesExistAsync(ApplicationDbContext context)
    {
        HashSet<string> existingNormalizedNames = (await context.Roles
            .AsNoTracking()
            .TagWithCallSite()
            .Select(r => r.NormalizedName!)
            .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<ApplicationRole> missingRoles = StandardRoles
            .Where(r => !existingNormalizedNames.Contains(r.Name.ToUpperInvariant()))
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