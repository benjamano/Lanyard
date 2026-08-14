using System.Security.Cryptography;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class DatabaseSeeder
{
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
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSeeder");

        await SeedUsersAndRolesAsync(context, configuration, logger);

        await EnsureStandardRolesExistAsync(context);

        await SeedCompanyAndLocationsAsync(context);

        await ResetIdentitySequencesAsync(context);
    }

    private static async Task SeedUsersAndRolesAsync(ApplicationDbContext context, IConfiguration configuration, ILogger logger)
    {
        if (await context.Users.AnyAsync(u => u.Id == ApplicationDbContext.SeedAdminUserId))
        {
            return;
        }

        PasswordHasher<UserProfile> passwordHasher = new();

        UserProfile seedAdminUser = new UserProfile
        {
            Id = ApplicationDbContext.SeedAdminUserId,
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            Email = "admin@play2day.com",
            NormalizedEmail = "ADMIN@PLAY2DAY.COM",
            EmailConfirmed = true,
            FirstName = "System",
            LastName = "Administrator",
            PasswordHash = null,
            SecurityStamp = "SEED-ADMIN-SECURITY-STAMP",
            ConcurrencyStamp = "SEED-ADMIN-CONCURRENCY-STAMP"
        };
        string? configuredPassword = configuration["Seed:AdminPassword"];
        string adminPassword;

        if (!string.IsNullOrWhiteSpace(configuredPassword))
        {
            adminPassword = configuredPassword;

            logger.LogInformation("Seeding admin user with the password supplied via Seed:AdminPassword.");
        }
        else
        {
            adminPassword = GenerateRandomPassword();

            logger.LogWarning(
                "No Seed:AdminPassword configured. Seeding admin user '{UserName}' with a generated "
                + "password: {Password}  --  log in and change it immediately, then set Seed__AdminPassword "
                + "or remove this account.", seedAdminUser.UserName, adminPassword);
        }

        seedAdminUser.PasswordHash = passwordHasher.HashPassword(seedAdminUser, adminPassword);

        await context.Users.AddAsync(seedAdminUser);

        await context.Roles.AddRangeAsync(StandardRoles.Select(r => new ApplicationRole
        {
            Id = r.Id,
            Name = r.Name,
            NormalizedName = r.Name.ToUpperInvariant(),
            ConcurrencyStamp = r.ConcurrencyStamp,
            CreatedByUserId = ApplicationDbContext.SeedAdminUserId,
            CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
            IsActive = true
        }));

        await context.UserRoles.AddRangeAsync(
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedAdminRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedManagerRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedStaffRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedCanControlMusicRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedCanClockInRoleId }
        );

        await context.SaveChangesAsync();
    }

    private static async Task EnsureStandardRolesExistAsync(ApplicationDbContext context)
    {
        if (!await context.Users.AnyAsync(u => u.Id == ApplicationDbContext.SeedAdminUserId))
        {
            return;
        }

        HashSet<string> existingNormalizedNames = (await context.Roles
            .Select(r => r.NormalizedName!)
            .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<ApplicationRole> missingRoles = StandardRoles
            .Where(r => !existingNormalizedNames.Contains(r.Name.ToUpperInvariant()))
            .Select(r => new ApplicationRole
            {
                Id = r.Id,
                Name = r.Name,
                NormalizedName = r.Name.ToUpperInvariant(),
                ConcurrencyStamp = r.ConcurrencyStamp,
                CreatedByUserId = ApplicationDbContext.SeedAdminUserId,
                CreateDate = DateTime.UtcNow,
                IsActive = true
            })
            .ToList();

        if (missingRoles.Count == 0)
        {
            return;
        }

        await context.Roles.AddRangeAsync(missingRoles);
        await context.SaveChangesAsync();
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

    private static string GenerateRandomPassword()
    {
        string random = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        return $"{random}Aa1!";
    }
}