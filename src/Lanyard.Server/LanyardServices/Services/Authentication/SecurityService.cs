using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;
using Lanyard.Application.Services.Training;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Authentication;

public class SecurityService : ISecurityService
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly UserManager<UserProfile> _userManager;
    private readonly ICourseAssignmentService _courseAssignmentService;
    private readonly ILogger<SecurityService> _logger;

    public SecurityService(
        AuthenticationStateProvider authStateProvider,
        IDbContextFactory<ApplicationDbContext> factory,
        UserManager<UserProfile> userManager,
        ICourseAssignmentService courseAssignmentService,
        ILogger<SecurityService> logger)
    {
        _authStateProvider = authStateProvider;
        _factory = factory;
        _userManager = userManager;
        _courseAssignmentService = courseAssignmentService;
        _logger = logger;
    }

    public async Task<Result<string>> GetCurrentUserIdAsync()
    {
        AuthenticationState authState = await _authStateProvider.GetAuthenticationStateAsync();
        ClaimsPrincipal user = authState.User;

        if (user is null || user.Identity == null)
        {
            return Result<string>.Fail("User information is not available");
        }

        if (user.Identity?.IsAuthenticated == false)
        {
            return Result<string>.Fail("User is not authenticated");
        }

        if (user.FindFirst(ClaimTypes.NameIdentifier) is null && user.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier") is null && user.FindFirst("sub") is null)
        {
            return Result<string>.Fail("User ID claim not found");
        }

        return Result<string>.Ok((user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value ?? user.FindFirst("sub")?.Value)!);
    }

    public async Task<bool> IsUserLoggedIn()
    {
        AuthenticationState authState = await _authStateProvider.GetAuthenticationStateAsync();
        return authState.User?.Identity?.IsAuthenticated == true;
    }

    public async Task<bool> IsCurrentUserInRoleAsync(string role)
    {
        AuthenticationState authState = await _authStateProvider.GetAuthenticationStateAsync();
        return authState.User?.Identity?.IsAuthenticated == true && authState.User.IsInRole(role);
    }

    public async Task<Result<UserProfile>> GetCurrentUserProfileAsync()
    {
        try{
            Result<string> getResult = await GetCurrentUserIdAsync();

            if (!getResult.IsSuccess || getResult.Data == null)
            {
                return Result<UserProfile>.Fail("User ID is not available");
            }

            using ApplicationDbContext ctx = _factory.CreateDbContext();
            UserProfile? user = await ctx.Users.FindAsync(getResult.Data);

            if (user is null)
            {
                return Result<UserProfile>.Fail("User not found");
            }

            return Result<UserProfile>.Ok(user);
        }
        catch (Exception ex)
        {
            return Result<UserProfile>.Fail(ex.Message);
        }
    }
    
    public async Task<string?> GetCurrentUserName()
    {
        Result<UserProfile> getResult = await GetCurrentUserProfileAsync();

        if (!getResult.IsSuccess || getResult.Data is null)
        {
            return null;
        }

        UserProfile user = getResult.Data;

        if (!string.IsNullOrEmpty(user.FirstName) && !string.IsNullOrEmpty(user.LastName))
        {
            return $"{user.FirstName} {user.LastName}";
        }

        return user.Email ?? user.UserName;
    }

    public async Task<IEnumerable<UserProfile>> GetAllUsersAsync()
    {
        using ApplicationDbContext ctx = _factory.CreateDbContext();
        return await ctx.Users.ToListAsync();
    }

    public async Task UpdateUserProfileAsync(UserProfile updatedUserProfile)
    {
        using ApplicationDbContext ctx = _factory.CreateDbContext();

        UserProfile? userProfile = await ctx.Users.FirstOrDefaultAsync(x => x.Id == updatedUserProfile.Id);
        if (userProfile is null) return;

        ctx.Entry(userProfile).CurrentValues.SetValues(updatedUserProfile);
        await ctx.SaveChangesAsync();
    }

    public async Task<IEnumerable<UserProfile>> GetActiveUsersAsync()
    {
        using ApplicationDbContext ctx = _factory.CreateDbContext();
        return await ctx.Users.ToListAsync();
    }

    public async Task<Result<UserCreationResult>> CreateUserAsync(UserProfile user)
    {
        try
        {
            if ((await GetActiveUsersAsync()).Any())
            {
                // Once at least one account exists, only an Admin may create further accounts —
                // being merely logged in is not enough (any Staff-level account could otherwise
                // create new accounts, including admin ones, for itself).
                if (!await IsCurrentUserInRoleAsync("Admin"))
                {
                    return Result<UserCreationResult>.Fail("You must be an administrator to perform this action!");
                }
            }

            if (string.IsNullOrWhiteSpace(user.FirstName) || string.IsNullOrWhiteSpace(user.LastName))
            {
                return Result<UserCreationResult>.Fail("The new user's first and last names are required!");
            }

            string initial = user.FirstName.ToLowerInvariant()[..1];
            string surname = user.LastName.ToLowerInvariant();
            user.UserName = initial + surname;

            string generatedPassword = GenerateRandomPassword();

            user.EmailConfirmed = true;

            IdentityResult result = await _userManager.CreateAsync(user, generatedPassword);

            if (!result.Succeeded)
            {
                string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return Result<UserCreationResult>.Fail($"Failed to create user: {errors}");
            }

            try
            {
                await using ApplicationDbContext autoAssignCtx = await _factory.CreateDbContextAsync();

                List<Guid> autoAssignCourseIds = await autoAssignCtx.Courses
                    .Where(x => x.IsActive && x.AutoAssignOnUserCreation)
                    .Select(x => x.Id)
                    .ToListAsync();

                foreach (Guid courseId in autoAssignCourseIds)
                {
                    await _courseAssignmentService.AssignCourseToUsersAsync(courseId, [user.Id], null, null);
                }
            }
            catch (Exception ex)
            {
                // Auto-assigning training courses must never block account creation —
                // a new hire needs their login regardless of whether their induction
                // course could be auto-assigned.
                _logger.LogWarning(ex, "Failed to auto-assign training courses to newly created user {UserId}", user.Id);
            }

            return Result<UserCreationResult>.Ok(new UserCreationResult(user, generatedPassword));
        }
        catch (Exception ex)
        {
            return Result<UserCreationResult>.Fail(ex.Message);
        }
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

    public async Task<Result<bool>> DeleteUserAsync(string userId)
    {
        try
        {
            if (!await IsCurrentUserInRoleAsync("Admin"))
            {
                return Result<bool>.Fail("You must be an administrator to perform this action!");
            }

            UserProfile? user = await _userManager.FindByIdAsync(userId);

            if (user is null)
            {
                return Result<bool>.Fail("User not found!");
            }

            IdentityResult result = await _userManager.DeleteAsync(user);

            if (!result.Succeeded)
            {
                string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return Result<bool>.Fail($"Failed to delete user: {errors}");
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> ChangePasswordAsync(string userId, string newPassword)
    {
        try
        {
            if (!await IsUserLoggedIn())
            {
                return Result<bool>.Fail("You must be logged in to perform this action!");
            }

            UserProfile? user = await _userManager.FindByIdAsync(userId);

            if (user is null)
            {
                return Result<bool>.Fail("User not found!");
            }

            string resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            IdentityResult result = await _userManager.ResetPasswordAsync(user, resetToken, newPassword);

            if (!result.Succeeded)
            {
                string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return Result<bool>.Fail($"Failed to change password: {errors}");
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }
}
