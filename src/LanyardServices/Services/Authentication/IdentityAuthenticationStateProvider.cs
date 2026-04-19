using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Lanyard.Application.Services.Authentication;

/// <summary>
/// Custom authentication state provider for Blazor Server that integrates with ASP.NET Core Identity
/// </summary>
public class IdentityAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IdentityAuthenticationStateProvider> _logger;

    public IdentityAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
        : base(loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _logger = loggerFactory.CreateLogger<IdentityAuthenticationStateProvider>();
    }

    /// <summary>
    /// Interval for revalidating the authentication state
    /// </summary>
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    /// <summary>
    /// Validates the current authentication state
    /// </summary>
    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get the user manager from the service provider
            using IServiceScope scope = _serviceProvider.CreateScope();
            UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
            
            // Get the user ID from claims
            string? userId = authenticationState.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            
            if (string.IsNullOrEmpty(userId))
            {
                return false;
            }

            // Check if the user still exists and is valid
            UserProfile? user = await userManager.FindByIdAsync(userId);
            return user != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating authentication state");
            return false;
        }
    }

    /// <summary>
    /// Gets the current authentication state
    /// </summary>
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        // This is handled by the base class and ASP.NET Core Identity
        return base.GetAuthenticationStateAsync();
    }
}
