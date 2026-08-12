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
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSeeder");

        await SeedUsersAndRolesAsync(context, configuration, logger);
        await SeedCompanyAndLocationsAsync(context);
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

        await context.Roles.AddRangeAsync(
            new ApplicationRole
            {
                Id = ApplicationDbContext.SeedAdminRoleId,
                Name = "Admin",
                NormalizedName = "ADMIN",
                ConcurrencyStamp = "SEED-ROLE-ADMIN-CS",
                CreatedByUserId = ApplicationDbContext.SeedAdminUserId,
                CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
                IsActive = true
            },
            new ApplicationRole
            {
                Id = ApplicationDbContext.SeedManagerRoleId,
                Name = "Manager",
                NormalizedName = "MANAGER",
                ConcurrencyStamp = "SEED-ROLE-MANAGER-CS",
                CreatedByUserId = ApplicationDbContext.SeedAdminUserId,
                CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
                IsActive = true
            },
            new ApplicationRole
            {
                Id = ApplicationDbContext.SeedStaffRoleId,
                Name = "Staff",
                NormalizedName = "STAFF",
                ConcurrencyStamp = "SEED-ROLE-STAFF-CS",
                CreatedByUserId = ApplicationDbContext.SeedAdminUserId,
                CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
                IsActive = true
            },
            new ApplicationRole
            {
                Id = ApplicationDbContext.SeedCanControlMusicRoleId,
                Name = "CanControlMusic",
                NormalizedName = "CANCONTROLMUSIC",
                ConcurrencyStamp = "SEED-ROLE-CAN-CONTROL-MUSIC-CS",
                CreatedByUserId = ApplicationDbContext.SeedAdminUserId,
                CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
                IsActive = true
            },
            new ApplicationRole
            {
                Id = ApplicationDbContext.SeedCanClockInRoleId,
                Name = "CanClockIn",
                NormalizedName = "CANCLOCKIN",
                ConcurrencyStamp = "SEED-ROLE-CAN-CLOCK-IN-CS",
                CreatedByUserId = ApplicationDbContext.SeedAdminUserId,
                CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
                IsActive = true
            },
            new ApplicationRole
            {
                Id = ApplicationDbContext.SeedCanManageDmxSystemsRoleId,
                Name = "CanManageDmxSystems",
                NormalizedName = "CANMANAGEDMXSYSTEMS",
                ConcurrencyStamp = "SEED-ROLE-CAN-MANAGE-DMX-SYSTEMS-CS",
                CreatedByUserId = ApplicationDbContext.SeedAdminUserId,
                CreateDate = ApplicationDbContext.SeedRoleCreateDateUtc,
                IsActive = true
            }
        );

        await context.UserRoles.AddRangeAsync(
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedAdminRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedManagerRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedStaffRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedCanControlMusicRoleId },
            new IdentityUserRole<string> { UserId = ApplicationDbContext.SeedAdminUserId, RoleId = ApplicationDbContext.SeedCanClockInRoleId }
        );

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

        await ResetIdentitySequencesAsync(context);
    }

    // Companies.Id and Locations.Id are Postgres GENERATED BY DEFAULT AS IDENTITY columns. The
    // seed above supplies explicit Ids (1, 1, 2), which Postgres accepts but which does NOT
    // advance the underlying sequence - it still sits at 1. Without this reset, the first
    // company or location added through the admin UI would be handed Id 1 again and fail with a
    // raw duplicate-key error. setval to MAX(Id) so the next generated value follows the seed.
    private static async Task ResetIdentitySequencesAsync(ApplicationDbContext context)
    {
        // Guarded because the test suite (and any in-memory host) has no Postgres behind it and
        // would throw on raw SQL.
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
        // 24 URL-safe random characters, with fixed complexity characters appended so the result
        // always satisfies ASP.NET Identity's default password rules (upper, lower, digit).
        string random = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        return $"{random}Aa1!";
    }
}