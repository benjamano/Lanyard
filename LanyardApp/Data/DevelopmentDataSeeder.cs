using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.App.Data;

/// <summary>
/// Seeds initial data for development environments
/// </summary>
public static class DevelopmentDataSeeder
{
    /// <summary>
    /// Seeds the database with initial admin user and roles for development
    /// </summary>
    public static async Task SeedDevelopmentDataAsync(
        IServiceProvider serviceProvider,
        IWebHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            return; // Only run in development
        }

        using IServiceScope scope = serviceProvider.CreateScope();
        IServiceProvider services = scope.ServiceProvider;

        try
        {
            UserManager<UserProfile> userManager = services.GetRequiredService<UserManager<UserProfile>>();
            RoleManager<ApplicationRole> roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
            ApplicationDbContext context = services.GetRequiredService<ApplicationDbContext>();
            ILogger<Program> logger = services.GetRequiredService<ILogger<Program>>();

            logger.LogInformation("?? Starting development data seeding...");

            // Ensure database is created
            await context.Database.MigrateAsync();

            // Create admin user first (so we can use their ID for role creation)
            UserProfile adminUser = await SeedAdminUserAsync(userManager, logger);

            // Create roles with admin as creator
            await SeedRolesAsync(roleManager, adminUser.Id, logger);

            // Assign roles to admin user
            await AssignRolesToAdminAsync(userManager, adminUser, logger);

            logger.LogInformation("? Development data seeding completed successfully!");
        }
        catch (Exception ex)
        {
            ILogger<Program> logger = services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "? An error occurred while seeding development data");
        }
    }

    private static async Task<UserProfile> SeedAdminUserAsync(
        UserManager<UserProfile> userManager,
        ILogger logger)
    {
        const string adminUsername = "admin";
        const string adminEmail = "admin@play2day.com";
        const string adminPassword = "Admin123!"; // Only for development!

        UserProfile? existingUser = await userManager.FindByNameAsync(adminUsername);

        if (existingUser == null)
        {
            UserProfile adminUser = new UserProfile
            {
                UserName = adminUsername,
                Email = adminEmail,
                EmailConfirmed = true,
                FirstName = "System",
                LastName = "Administrator"
            };

            IdentityResult result = await userManager.CreateAsync(adminUser, adminPassword);

            if (result.Succeeded)
            {
                logger.LogInformation("  ? Created admin user: {Username}", adminUsername);

                logger.LogWarning("");
                logger.LogWarning("==========================================");
                logger.LogWarning("   ?? DEVELOPMENT ADMIN CREDENTIALS");
                logger.LogWarning("==========================================");
                logger.LogWarning("   Username: {Username}", adminUsername);
                logger.LogWarning("   Password: {Password}", adminPassword);
                logger.LogWarning("   Email: {Email}", adminEmail);
                logger.LogWarning("==========================================");
                logger.LogWarning("   ??  FOR DEVELOPMENT USE ONLY!");
                logger.LogWarning("   This account is created automatically");
                logger.LogWarning("   in development environments.");
                logger.LogWarning("==========================================");
                logger.LogWarning("");

                return adminUser;
            }
            else
            {
                string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                logger.LogError("  ? Failed to create admin user: {Errors}", errors);
                throw new InvalidOperationException($"Failed to create admin user: {errors}");
            }
        }
        else
        {
            logger.LogInformation("  ? Admin user already exists: {Username}", adminUsername);
            return existingUser;
        }
    }

    private static async Task SeedRolesAsync(
        RoleManager<ApplicationRole> roleManager,
        string createdByUserId,
        ILogger logger)
    {
        string[] roles = { "Admin", "Manager", "Staff", "CanControlMusic", "CanClockIn" };

        foreach (string roleName in roles)
        {
            bool roleExists = await roleManager.RoleExistsAsync(roleName);
            
            if (!roleExists)
            {
                ApplicationRole role = new ApplicationRole
                {
                    Name = roleName,
                    CreatedByUserId = createdByUserId, // Created by admin user
                    CreateDate = DateTime.UtcNow,
                    IsActive = true
                };

                IdentityResult result = await roleManager.CreateAsync(role);

                if (result.Succeeded)
                {
                    logger.LogInformation("  ? Created role: {RoleName}", roleName);
                }
                else
                {
                    string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    logger.LogWarning("  ?? Failed to create role {RoleName}: {Errors}", roleName, errors);
                }
            }
            else
            {
                logger.LogInformation("  ? Role already exists: {RoleName}", roleName);
            }
        }
    }

    private static async Task AssignRolesToAdminAsync(
        UserManager<UserProfile> userManager,
        UserProfile adminUser,
        ILogger logger)
    {
        string[] roles = { "Admin", "Manager", "Staff", "CanControlMusic", "CanClockIn" };
        
        foreach (string role in roles)
        {
            bool isInRole = await userManager.IsInRoleAsync(adminUser, role);
            if (!isInRole)
            {
                IdentityResult result = await userManager.AddToRoleAsync(adminUser, role);
                if (result.Succeeded)
                {
                    logger.LogInformation("  ? Added role {Role} to admin user", role);
                }
                else
                {
                    string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    logger.LogWarning("  ?? Failed to add role {Role}: {Errors}", role, errors);
                }
            }
            else
            {
                logger.LogInformation("  ? Admin already has role: {Role}", role);
            }
        }
    }
}
