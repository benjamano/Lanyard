using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using LanyardData.DataAccess;
using LanyardData.DTO;
using LanyardData.Models;
using System.Security.Claims;

namespace LanyardAPI.Services
{
    public class SecurityService(AuthenticationStateProvider AuthStateProvider, IDbContextFactory<ApplicationDbContext> factory)
    {
        private readonly AuthenticationStateProvider AuthStateProvider = AuthStateProvider;
        private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

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

            return await ctx.Users
                .ToListAsync();
        }

        public async Task<Result<UserProfile>> CreateUserAsync(UserProfile user)
        {
            try
            {
                if (!await IsUserLoggedIn())
                {
                    return Result<UserProfile>.Fail("You must be logged in to perform this action!");
                }

                if (string.IsNullOrWhiteSpace(user.FirstName) == true || string.IsNullOrWhiteSpace(user.LastName) == true)
                {
                    return Result<UserProfile>.Fail("The new user's first and last names are required!");
                }

                using ApplicationDbContext ctx = _factory.CreateDbContext();

                string initial = user.FirstName?.ToLowerInvariant()[..1] ?? "";
                string surname = user.LastName?.ToLowerInvariant() ?? "";

                user.UserName = initial + surname;

                ctx.Add(user);

                await ctx.SaveChangesAsync();

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

                using ApplicationDbContext ctx = _factory.CreateDbContext();

                UserProfile? user = await ctx.Users.FirstOrDefaultAsync(x => x.Id == userId);

                if (user is null)
                {
                    return Result<bool>.Fail("User not found!");
                }

                ctx.Users.Remove(user);

                await ctx.SaveChangesAsync();

                return Result<bool>.Ok(true);
            }
            catch (Exception ex)
            {
                return Result<bool>.Fail(ex.Message);
            }
        }
    }
}
