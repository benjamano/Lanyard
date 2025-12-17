using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using LanyardData.DataAccess;
using LanyardData.DTO;
using LanyardData.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;

namespace LanyardAPI.Services
{
    public class SecurityService
    {
        private readonly AuthenticationStateProvider AuthStateProvider;
        private readonly IDbContextFactory<ApplicationDbContext> _factory;
        private readonly UserManager<UserProfile> _userManager;

        public SecurityService(
            AuthenticationStateProvider authStateProvider, 
            IDbContextFactory<ApplicationDbContext> factory,
            UserManager<UserProfile> userManager)
        {
            AuthStateProvider = authStateProvider;
            _factory = factory;
            _userManager = userManager;
        }

        public async Task<string?> GetCurrentUserIdAsync()
        {
            AuthenticationState authState = await AuthStateProvider.GetAuthenticationStateAsync();
            ClaimsPrincipal user = authState.User;

            if (user.Identity is not null && user.Identity.IsAuthenticated)
            {
                return user.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value
                    ?? user.FindFirst("oid")?.Value
                    ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            }

            return null;
        }

        public async Task<bool> IsUserLoggedIn()
        {
            return await GetCurrentUserIdAsync() != null;
        }

        public async Task<UserProfile?> GetCurrentUserProfileAsync()
        {
            string? userId = await GetCurrentUserIdAsync();

            if (userId is not null)
            {
                using ApplicationDbContext ctx = _factory.CreateDbContext();

                return await ctx.Users
                    .AsNoTracking()
                    .Where(x => x.Id == userId)
                    .FirstOrDefaultAsync();
            }

            return null;
        }

        public async Task<string?> GetCurrentUserName()
        {
            UserProfile? user = await GetCurrentUserProfileAsync();

            if (user is null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(user?.FirstName) || string.IsNullOrEmpty(user?.LastName))
            {
                return user?.Email;
            }
            else
            {
                return user?.FirstName + " " + user?.LastName;
            }
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

                string initial = user.FirstName?.ToLowerInvariant()[..1] ?? "";
                string surname = user.LastName?.ToLowerInvariant() ?? "";
                user.UserName = initial + surname;

                string generatedPassword = "changeME1234!"; //GenerateSecurePassword();

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

        private static string GenerateSecurePassword(int length = 16)
        {
            const string uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            const string lowercase = "abcdefghijklmnopqrstuvwxyz";
            const string digits = "0123456789";
            const string special = "!@#$%^&*()_+-=[]{}|;:,.<>?";
            const string allChars = uppercase + lowercase + digits + special;

            char[] password = new char[length];

            password[0] = uppercase[RandomNumberGenerator.GetInt32(uppercase.Length)];
            password[1] = lowercase[RandomNumberGenerator.GetInt32(lowercase.Length)];
            password[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
            password[3] = special[RandomNumberGenerator.GetInt32(special.Length)];

            for (int i = 4; i < length; i++)
            {
                password[i] = allChars[RandomNumberGenerator.GetInt32(allChars.Length)];
            }

            for (int i = password.Length - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1);
                (password[i], password[j]) = (password[j], password[i]);
            }

            return new string(password);
        }
    }
}
