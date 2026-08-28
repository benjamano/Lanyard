using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace Lanyard.Application.Services.Authentication;

/// <summary>
/// Resolves the signed-in user's id from the auth state, and nothing else.
/// </summary>
/// <remarks>
/// Extracted out of <see cref="ISecurityService"/> so that services needing only
/// "who is calling" don't have to depend on the whole of SecurityService - which
/// transitively pulls in course assignment, email and company services. FileService
/// taking that whole dependency for two convenience overloads is what turned
/// CertificateService's use of IFileService into a DI cycle:
/// SecurityService -> CourseAssignmentService -> CertificateService -> FileService
/// -> SecurityService. Nothing ever called around that loop at runtime, but the
/// container validates the type graph, not the call graph.
/// </remarks>
public interface ICurrentUserAccessor
{
    Task<Result<string>> GetCurrentUserIdAsync();
}

public class CurrentUserAccessor(AuthenticationStateProvider authStateProvider) : ICurrentUserAccessor
{
    private readonly AuthenticationStateProvider _authStateProvider = authStateProvider;

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

        string? userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value
            ?? user.FindFirst("sub")?.Value;

        if (userId is null)
        {
            return Result<string>.Fail("User ID claim not found");
        }

        return Result<string>.Ok(userId);
    }
}
