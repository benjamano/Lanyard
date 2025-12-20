using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.ApplicationRoles;

public class ApplicationRolesService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly SecurityService _sApi;
    private readonly UserManager<UserProfile> _umApi;
    private readonly RoleManager<ApplicationRole> _rmApi;

    public ApplicationRolesService(
        IDbContextFactory<ApplicationDbContext> factory,
        SecurityService sApi,
        UserManager<UserProfile> userManager,
        RoleManager<ApplicationRole> roleManager)
    {
        _factory = factory;
        _sApi = sApi;
        _umApi = userManager;
        _rmApi = roleManager;
    }

    public async Task<Result<List<ApplicationRole>>> GetAllApplicationRolesAsync()
    {
        try
        {
            using ApplicationDbContext ctx = _factory.CreateDbContext();

            List<ApplicationRole> roles = await ctx.Roles
                .Include(x => x.CreatedByUser)
                .Where(x => x.IsActive)
                .ToListAsync();

            return Result<List<ApplicationRole>>.Ok(roles);
        }
        catch (Exception ex)
        {
            return Result<List<ApplicationRole>>.Fail($"Failed to retrieve roles: {ex.Message}");
        }
    }

    public async Task<Result<bool>> CreateNewRoleAsync(string roleName)
    {
        if (!await _sApi.IsUserLoggedIn())
        {
            return Result<bool>.Fail("You must be logged in to perform this action!");
        }

        try
        {
            ApplicationRole? existingRole = await _rmApi.FindByNameAsync(roleName);

            if (existingRole is not null)
            {
                if (existingRole.IsActive == false)
                {
                    existingRole.IsActive = true;

                    IdentityResult updateResult = await _rmApi.UpdateAsync(existingRole);
                    if (!updateResult.Succeeded)
                    {
                        return Result<bool>.Fail("Failed to reactivate role.");
                    }

                    return Result<bool>.Ok(true);
                }

                return Result<bool>.Fail("Role already exists and is active!");
            }

            string? currentUserId = await _sApi.GetCurrentUserIdAsync();
            if (string.IsNullOrEmpty(currentUserId))
            {
                return Result<bool>.Fail("Unable to determine current user.");
            }

            ApplicationRole newRole = new ApplicationRole
            {
                Name = roleName,
                CreateDate = DateTime.UtcNow,
                CreatedByUserId = currentUserId,
                IsActive = true
            };

            IdentityResult result = await _rmApi.CreateAsync(newRole);

            if (!result.Succeeded)
            {
                string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return Result<bool>.Fail($"Failed to create role: {errors}");
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to create role: {ex.Message}");
        }
    }

    public async Task<Result<List<string>>> GetUserRoleNamesAsync(string userId)
    {
        try
        {
            UserProfile? user = await _umApi.FindByIdAsync(userId);

            if (user is null)
            {
                return Result<List<string>>.Fail("User not found.");
            }

            IList<string> roleNames = await _umApi.GetRolesAsync(user);

            using ApplicationDbContext ctx = _factory.CreateDbContext();
            List<string> activeRoleNames = await ctx.Roles
                .Where(r => r.IsActive && roleNames.Contains(r.Name!))
                .Select(r => r.Name!)
                .ToListAsync();

            return Result<List<string>>.Ok(activeRoleNames);
        }
        catch (Exception ex)
        {
            return Result<List<string>>.Fail($"Failed to retrieve user roles: {ex.Message}");
        }
    }

    public async Task<Result<List<ApplicationRole>>> GetUserRolesAsync(string userId)
    {
        try
        {
            UserProfile? user = await _umApi.FindByIdAsync(userId);

            if (user is null)
            {
                return Result<List<ApplicationRole>>.Fail("User not found.");
            }

            IList<string> roleNames = await _umApi.GetRolesAsync(user);

            using ApplicationDbContext ctx = _factory.CreateDbContext();
            List<ApplicationRole> activeRoles = await ctx.Roles
                .Include(r => r.CreatedByUser)
                .Where(r => r.IsActive && roleNames.Contains(r.Name!))
                .ToListAsync();

            return Result<List<ApplicationRole>>.Ok(activeRoles);
        }
        catch (Exception ex)
        {
            return Result<List<ApplicationRole>>.Fail($"Failed to retrieve user roles: {ex.Message}");
        }
    }

    public async Task<Result<string>> AssignRoleToUserAsync(string userId, string roleId)
    {
        if (!await _sApi.IsUserLoggedIn())
        {
            return Result<string>.Fail("You must be logged in to perform this action!");
        }

        try
        {
            UserProfile? user = await _umApi.FindByIdAsync(userId);
            ApplicationRole? role = await _rmApi.FindByIdAsync(roleId);

            if (user is null)
            {
                return Result<string>.Fail("User not found.");
            }

            if (role is null)
            {
                return Result<string>.Fail("Role not found.");
            }

            if (!role.IsActive)
            {
                return Result<string>.Fail("Cannot assign an inactive role.");
            }

            if (await _umApi.IsInRoleAsync(user, role.Name!))
            {
                return Result<string>.Ok(user.Id);
            }

            IdentityResult identityResult = await _umApi.AddToRoleAsync(user, role.Name!);

            if (!identityResult.Succeeded)
            {
                string errors = string.Join(", ", identityResult.Errors.Select(e => e.Description));
                return Result<string>.Fail($"Failed to assign role: {errors}");
            }

            return Result<string>.Ok(user.Id);
        }
        catch (Exception ex)
        {
            return Result<string>.Fail($"Failed to assign role to user: {ex.Message}");
        }
    }

    public async Task<Result<string>> RemoveRoleFromUserAsync(string userId, string roleId)
    {
        if (!await _sApi.IsUserLoggedIn())
        {
            return Result<string>.Fail("You must be logged in to perform this action!");
        }

        try
        {
            UserProfile? user = await _umApi.FindByIdAsync(userId);
            ApplicationRole? role = await _rmApi.FindByIdAsync(roleId);

            if (user is null)
            {
                return Result<string>.Fail("User not found.");
            }

            if (role is null)
            {
                return Result<string>.Fail("Role not found.");
            }

            if (!await _umApi.IsInRoleAsync(user, role.Name!))
            {
                return Result<string>.Ok(user.Id);
            }

            IdentityResult identityResult = await _umApi.RemoveFromRoleAsync(user, role.Name!);

            if (!identityResult.Succeeded)
            {
                string errors = string.Join(", ", identityResult.Errors.Select(e => e.Description));
                return Result<string>.Fail($"Failed to remove role: {errors}");
            }

            return Result<string>.Ok(user.Id);
        }
        catch (Exception ex)
        {
            return Result<string>.Fail($"Failed to remove role from user: {ex.Message}");
        }
    }

    public async Task<Result<List<string>>> DeleteRoleAsync(string roleId)
    {
        if (!await _sApi.IsUserLoggedIn())
        {
            return Result<List<string>>.Fail("You must be logged in to perform this action!");
        }

        try
        {
            ApplicationRole? role = await _rmApi.FindByIdAsync(roleId);

            if (role is null)
            {
                return Result<List<string>>.Fail("Role not found.");
            }

            IList<UserProfile> usersInRole = await _umApi.GetUsersInRoleAsync(role.Name!);
            List<string> affectedUserIds = new List<string>();

            foreach (UserProfile user in usersInRole)
            {
                IdentityResult removeResult = await _umApi.RemoveFromRoleAsync(user, role.Name!);
                if (!removeResult.Succeeded)
                {
                    string errors = string.Join(", ", removeResult.Errors.Select(e => e.Description));
                    return Result<List<string>>.Fail($"Failed to remove role from user {user.UserName}: {errors}");
                }

                affectedUserIds.Add(user.Id);
            }

            role.IsActive = false;

            IdentityResult updateResult = await _rmApi.UpdateAsync(role);

            if (!updateResult.Succeeded)
            {
                string errors = string.Join(", ", updateResult.Errors.Select(e => e.Description));
                return Result<List<string>>.Fail($"Failed to mark role as inactive: {errors}");
            }

            return Result<List<string>>.Ok(affectedUserIds);
        }
        catch (Exception ex)
        {
            return Result<List<string>>.Fail($"Failed to delete role: {ex.Message}");
        }
    }
}
