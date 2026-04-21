using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;

namespace Lanyard.Application.Services.Authentication;

public class SecurityService : ISecurityService
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly UserManager<UserProfile> _userManager;

    public SecurityService(
        AuthenticationStateProvider authStateProvider, 
        IDbContextFactory<ApplicationDbContext> factory,
        UserManager<UserProfile> userManager)
    {
        _authStateProvider = authStateProvider;
        _factory = factory;
        _userManager = userManager;
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

    public async Task<Result<UserProfile>> GetCurrentUserProfileAsync()
    {
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

    public async Task<Result<UserProfile>> CreateUserAsync(UserProfile user)
    {
        try
        {
            if ((await GetActiveUsersAsync()).Any())
            {
                if (!await IsUserLoggedIn())
                {
                    return Result<UserProfile>.Fail("You must be logged in to perform this action!");
                }
            }

            if (string.IsNullOrWhiteSpace(user.FirstName) || string.IsNullOrWhiteSpace(user.LastName))
            {
                return Result<UserProfile>.Fail("The new user's first and last names are required!");
            }

            string initial = user.FirstName.ToLowerInvariant()[..1];
            string surname = user.LastName.ToLowerInvariant();
            user.UserName = initial + surname;

            string generatedPassword = "changeME1234!";

            user.EmailConfirmed = true;

            IdentityResult result = await _userManager.CreateAsync(user, generatedPassword);

            if (!result.Succeeded)
            {
                string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return Result<UserProfile>.Fail($"Failed to create user: {errors}");
            }

            return Result<UserProfile>.Ok(user);
        }
        catch (Exception ex)
        {
            return Result<UserProfile>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> DeleteUserAsync(string userId)
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
