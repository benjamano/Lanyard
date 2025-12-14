using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using LanyardData.Models;
using LanyardData.DataAccess;
using System.Security.Claims;
using System.Threading;

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

        public async Task UpdateUserProfile(UserProfile updatedUserProfile)
        {
            using ApplicationDbContext ctx = _factory.CreateDbContext();

            UserProfile? userProfile = await ctx.Users.FirstOrDefaultAsync(x => x.Id == updatedUserProfile.Id);
            if (userProfile is null) return;

            ctx.Entry(userProfile).CurrentValues.SetValues(updatedUserProfile);
            await ctx.SaveChangesAsync();

        }
    }
}
