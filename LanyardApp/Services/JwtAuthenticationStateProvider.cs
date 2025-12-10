using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace LanyardApp.Services
{
    public class JwtAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly TokenStorageService _tokenStorage;
        private readonly JwtTokenService _jwtTokenService;

        public JwtAuthenticationStateProvider(TokenStorageService tokenStorage, JwtTokenService jwtTokenService)
        {
            _tokenStorage = tokenStorage;
            _jwtTokenService = jwtTokenService;
        }

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var token = await _tokenStorage.GetTokenAsync();

            if (string.IsNullOrWhiteSpace(token))
            {
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            }

            var principal = _jwtTokenService.ValidateToken(token);

            if (principal == null)
            {
                await _tokenStorage.RemoveTokenAsync();
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            }

            return new AuthenticationState(principal);
        }

        public void NotifyAuthenticationStateChanged()
        {
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
    }
}
