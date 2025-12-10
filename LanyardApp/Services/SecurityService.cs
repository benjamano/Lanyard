using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using LanyardData.Models;
using LanyardData.DataAccess;
using System.Security.Claims;
using System.Threading;

namespace LanyardApp.Services
{
    public class SecurityService(AuthenticationStateProvider AuthStateProvider, ApplicationDbContext context)
    {
        private readonly AuthenticationStateProvider AuthStateProvider = AuthStateProvider;
        private readonly ApplicationDbContext _context = context;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private UserProfile? _user;
        private bool _isLoaded;

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

        public async Task<UserProfile?> GetCurrentUserProfileAsync()
        {
            if (_isLoaded)
            {
                return _user;
            }

            await _lock.WaitAsync();
            try
            {
                if (_isLoaded)
                {
                    return _user;
                }

                string? userId = await GetCurrentUserIdAsync();

                if (userId is not null)
                {
                    _user = await _context.Users
                        .Where(x=> x.Id == userId)
                        .FirstOrDefaultAsync();
                }

                _isLoaded = true;
                return _user;
            }
            finally
            {
                _lock.Release();
            }
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
    }
}
