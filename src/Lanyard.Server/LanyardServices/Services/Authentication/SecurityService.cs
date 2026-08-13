using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Training;
using Lanyard.Infrastructure.Models;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Training;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Lanyard.Infrastructure.Branding;

namespace Lanyard.Application.Services.Authentication;

public class SecurityService : ISecurityService
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly UserManager<UserProfile> _userManager;
    private readonly ICourseAssignmentService _courseAssignmentService;
    private readonly ICourseService _courseService;
    private readonly ILogger<SecurityService> _logger;
    private readonly NavigationManager _navigationManager;
    private readonly IEmailService _emailService;
    private readonly ICompanyLocationService _companyLocationService;
    private readonly IOptions<EmailOptions> _emailOptions;

    public SecurityService(
        AuthenticationStateProvider authStateProvider,
        IDbContextFactory<ApplicationDbContext> factory,
        UserManager<UserProfile> userManager,
        ICourseAssignmentService courseAssignmentService,
        ICourseService courseService,
        ILogger<SecurityService> logger,
        NavigationManager navigationManager,
        IEmailService emailService,
        ICompanyLocationService companyLocationService,
        IOptions<EmailOptions> emailOptions)
    {
        _authStateProvider = authStateProvider;
        _factory = factory;
        _userManager = userManager;
        _courseAssignmentService = courseAssignmentService;
        _courseService = courseService;
        _logger = logger;
        _navigationManager = navigationManager;
        _emailService = emailService;
        _companyLocationService = companyLocationService;
        _emailOptions = emailOptions;
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
        try
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

    public async Task<Result<UserCreationResult>> CreateUserAsync(UserProfile user, List<int> locationIds)
    {
        try
        {
            if ((await GetActiveUsersAsync()).Any())
            {
                // Once at least one account exists, only an Admin may create further accounts -
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

            if (string.IsNullOrWhiteSpace(user.Email) || !new EmailAddressAttribute().IsValid(user.Email))
            {
                return Result<UserCreationResult>.Fail("A valid email address is required to invite a new user.");
            }

            if (locationIds is null || locationIds.Count == 0)
            {
                return Result<UserCreationResult>.Fail("At least one location is required.");
            }

            string initial = user.FirstName.ToLowerInvariant()[..1];
            string surname = user.LastName.ToLowerInvariant();
            user.UserName = initial + surname;
            user.EmailConfirmed = true;
            user.InvitedDate = DateTime.UtcNow;

            // No password is set here — the invitee sets their own via the emailed link.
            IdentityResult result = await _userManager.CreateAsync(user);

            if (!result.Succeeded)
            {
                string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return Result<UserCreationResult>.Fail($"Failed to create user: {errors}");
            }

            foreach (int locationId in locationIds)
            {
                Result<bool> membershipResult = await _companyLocationService.AddUserToLocationAsync(user.Id, locationId);

                if (!membershipResult.IsSuccess)
                {
                    _logger.LogWarning("Failed to add newly created user {UserId} to location {LocationId}: {Error}", user.Id, locationId, membershipResult.Error);
                }
            }

            try
            {
                // Unscoped: auto-assignment on user creation applies across all locations,
                // matching pre-Task-3 behaviour where GetCoursesAsync() saw every course.
                Result<List<Course>> coursesResult = await _courseService.GetCoursesAsync(new LocationScope(true, null, null, null));

                if (coursesResult.IsSuccess && coursesResult.Data is not null)
                {
                    foreach (Course course in coursesResult.Data.Where(x => x.AutoAssignOnUserCreation))
                    {
                        // Unscoped: mirrors the GetCoursesAsync() call above - auto-assignment on
                        // user creation applies across all locations, matching pre-Task-4 behaviour.
                        Result<BulkAssignResult> assignResult = await _courseAssignmentService.AssignCourseToUsersAsync(course.Id, [user.Id], null, null, new LocationScope(true, null, null, null));

                        if (!assignResult.IsSuccess)
                        {
                            _logger.LogWarning("Failed to auto-assign course {CourseId} to new user {UserId}: {Error}", course.Id, user.Id, assignResult.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Auto-assigning training courses must never block account creation -
                // a new hire needs their login regardless of whether their induction
                // course could be auto-assigned.
                _logger.LogWarning(ex, "Failed to auto-assign training courses to newly created user {UserId}", user.Id);
            }

            Result<bool> emailResult = await SendSetPasswordLinkEmailAsync(user);

            return Result<UserCreationResult>.Ok(
                emailResult.IsSuccess
                    ? new UserCreationResult(user, EmailSent: true)
                    : new UserCreationResult(user, EmailSent: false, EmailError: emailResult.Error));
        }
        catch (Exception ex)
        {
            return Result<UserCreationResult>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SendSetPasswordLinkAsync(string userId)
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

            user.InvitedDate = DateTime.UtcNow;
            Result<bool> emailResult = await SendSetPasswordLinkEmailAsync(user);
            if (!emailResult.IsSuccess)
            {
                return Result<bool>.Fail($"Failed to send email: {emailResult.Error}");
            }

            await _userManager.UpdateAsync(user);
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SetPasswordFromTokenAsync(string userId, string token, string newPassword)
    {
        try
        {
            UserProfile? user = await _userManager.FindByIdAsync(userId);
            if (user is null)
            {
                return Result<bool>.Fail("This link is invalid or has expired.");
            }

            // No "already used" gate here by design: a successful ResetPasswordAsync rotates the
            // user's security stamp, which is baked into the token's own validation - so a given
            // token is inherently single-use regardless of PasswordSetDate. This is what lets the
            // same link mechanism serve both first-time invites and later admin-triggered password
            // resets for already-active accounts.
            IdentityResult result = await _userManager.ResetPasswordAsync(user, token, newPassword);
            if (!result.Succeeded)
            {
                bool isTokenError = result.Errors.Any(e => e.Code is "InvalidToken");
                string message = isTokenError
                    ? "This link is invalid or has expired. Ask an administrator to send you a new one."
                    : string.Join(", ", result.Errors.Select(e => e.Description));

                return Result<bool>.Fail(message);
            }

            user.PasswordSetDate = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<string>> GetUsernameForSetPasswordAsync(string userId)
    {
        UserProfile? user = await _userManager.FindByIdAsync(userId);

        if (user is null || string.IsNullOrEmpty(user.UserName))
        {
            return Result<string>.Fail("This link is invalid or has expired.");
        }

        return Result<string>.Ok(user.UserName);
    }

    private async Task<Result<bool>> SendSetPasswordLinkEmailAsync(UserProfile user)
    {
        string token = await _userManager.GeneratePasswordResetTokenAsync(user);

        // Both URLs below are consumed by a mail client on someone else's machine, so they must
        // be absolute and externally reachable. NavigationManager.BaseUri is the host of whatever
        // request happens to be running (localhost in dev, an internal hostname behind the proxy
        // in production) and is therefore wrong here - use the configured public base URL, the
        // same value CourseRecurrenceHostedService builds its email links from. BaseUri remains
        // the fallback only when PublicBaseUrl has not been configured at all.
        string baseUrl = string.IsNullOrWhiteSpace(_emailOptions.Value.PublicBaseUrl)
            ? _navigationManager.BaseUri.TrimEnd('/')
            : _emailOptions.Value.PublicBaseUrl.TrimEnd('/');

        string setPasswordUrl = $"{baseUrl}/set-password?userId={Uri.EscapeDataString(user.Id)}&token={Uri.EscapeDataString(token)}";

        string? logoUrl = null;
        string accentColorHex = BrandConstants.PrimaryColorHex;

        Result<List<Location>> locationsResult = await _companyLocationService.GetLocationsForUserAsync(user.Id);

        // Known, accepted limitation: a user who belongs to locations across more than one company
        // gets an arbitrary company's branding here - GetLocationsForUserAsync orders by location
        // name, so locations[0] carries no precedence meaning. Invite emails are not worth a
        // "primary company" concept; the branding is cosmetic and the link itself is unaffected.
        if (locationsResult.IsSuccess && locationsResult.Data is { Count: > 0 } locations && locations[0].Company is Company company)
        {
            accentColorHex = BrandConstants.ResolveAccentColor(company.ThemeColorHex);

            if (company.LogoFileId is Guid logoFileId)
            {
                // See MainLayout.ApplyBrandingAsync - the endpoint is cache-keyed by URL, so a
                // logo replacement needs a new URL to guarantee a fresh fetch.
                logoUrl = $"{baseUrl}/api/companies/{company.Id}/logo?v={logoFileId:N}";
            }
        }

        return await _emailService.SendSetPasswordEmailAsync(user, setPasswordUrl, logoUrl, accentColorHex);
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

            try
            {
                Result<int> cleanupResult = await _courseAssignmentService.UnassignAllForUserAsync(userId);

                if (!cleanupResult.IsSuccess)
                {
                    _logger.LogWarning("Failed to clean up CourseAssignments for deleted user {UserId}: {Error}", userId, cleanupResult.Error);
                }
            }
            catch (Exception ex)
            {
                // The user is already deleted at this point — this is best-effort
                // cleanup of their now-orphaned CourseAssignments rows and must
                // never undo or fail the deletion that already succeeded.
                _logger.LogWarning(ex, "Failed to clean up CourseAssignments for deleted user {UserId}", userId);
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
