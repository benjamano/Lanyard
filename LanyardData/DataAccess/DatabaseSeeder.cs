using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public static class DatabaseSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await SeedUsersAndRolesAsync(context);
    }

    private static async Task SeedUsersAndRolesAsync(ApplicationDbContext context)
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

        seedAdminUser.PasswordHash = passwordHasher.HashPassword(seedAdminUser, "Admin123!");

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
}