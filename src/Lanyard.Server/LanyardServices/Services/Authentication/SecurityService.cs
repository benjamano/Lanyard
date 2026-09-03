using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Training;
using Lanyard.Infrastructure.Models;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Training;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Lanyard.Infrastructure.Branding;
using QRCoder;
using System.Text;

namespace Lanyard.Application.Services.Authentication;

public class SecurityService : ISecurityService
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly ICurrentUserAccessor _currentUserAccessor;
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
        ICurrentUserAccessor currentUserAccessor,
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
        _currentUserAccessor = currentUserAccessor;
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

    // Delegates to ICurrentUserAccessor so this and FileService can never disagree about
    // which claim identifies the user. Kept on ISecurityService because plenty of callers
    // already reach for it here.
    public Task<Result<string>> GetCurrentUserIdAsync()
    {
        return _currentUserAccessor.GetCurrentUserIdAsync();
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

    private async Task<bool> IsCurrentUserAdminOrManagerAsync() =>
        await IsCurrentUserInRoleAsync("Admin") || await IsCurrentUserInRoleAsync("Manager");

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
        return await ctx.Users
            .Where(u => u.Id != ApplicationDbContext.SystemDeletedUserPlaceholderId)
            .ToListAsync();
    }

    public async Task<Result<UserCreationResult>> CreateUserAsync(UserProfile user, List<int> locationIds)
    {
        try
        {
            if ((await GetActiveUsersAsync()).Any())
            {
                // Once at least one account exists, only an Admin or Manager may create further
                // accounts - being merely logged in is not enough (any Staff-level account could
                // otherwise create new accounts, including admin ones, for itself).
                if (!await IsCurrentUserAdminOrManagerAsync())
                {
                    return Result<UserCreationResult>.Fail("You must be an administrator or manager to perform this action!");
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

            // No password is set here; the invitee sets their own via the emailed link.
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
                Result<List<Course>> coursesResult = await _courseService.GetCoursesAsync(new LocationScope(true, null, null, null), allLocations: true);

                if (coursesResult.IsSuccess && coursesResult.Data is not null)
                {
                    foreach (Course course in coursesResult.Data.Where(x => x.AutoAssignOnUserCreation))
                    {
                        // Unscoped: mirrors the GetCoursesAsync() call above - auto-assignment on
                        // user creation applies across all locations, matching pre-Task-4 behaviour.
                        // sendAssignedEmail: false - the user already gets a welcome/set-password
                        // email in this same flow; a second "training assigned" email would be noise.
                        Result<BulkAssignResult> assignResult = await _courseAssignmentService.AssignCourseToUsersAsync(course.Id, [user.Id], null, null, new LocationScope(true, null, null, null), sendAssignedEmail: false);

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
            if (!await IsCurrentUserAdminOrManagerAsync())
            {
                return Result<bool>.Fail("You must be an administrator or manager to perform this action!");
            }

            UserProfile? user = await _userManager.FindByIdAsync(userId);
            if (user is null)
            {
                return Result<bool>.Fail("User not found!");
            }

            if (!await IsCurrentUserInRoleAsync("Admin") && await _userManager.IsInRoleAsync(user, "Admin"))
            {
                return Result<bool>.Fail("Only an administrator can perform this action on an administrator account.");
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
        string? locationName = null;

        Result<List<Location>> locationsResult = await _companyLocationService.GetLocationsForUserAsync(user.Id);

        // Known, accepted limitation: a user who belongs to locations across more than one company
        // gets an arbitrary company's branding here - GetLocationsForUserAsync orders by location
        // name, so locations[0] carries no precedence meaning. Invite emails are not worth a
        // "primary company" concept; the branding is cosmetic and the link itself is unaffected.
        // The same limitation now applies to the location name shown in the email body.
        if (locationsResult.IsSuccess && locationsResult.Data is { Count: > 0 } locations)
        {
            locationName = locations[0].GetDisplayName();

            if (locations[0].Company is Company company)
            {
                accentColorHex = BrandConstants.ResolveAccentColor(company.ThemeColorHex);

                if (company.LogoFileId is Guid logoFileId)
                {
                    // See MainLayout.ApplyBrandingAsync - the endpoint is cache-keyed by URL, so a
                    // logo replacement needs a new URL to guarantee a fresh fetch.
                    logoUrl = $"{baseUrl}/api/companies/{company.Id}/logo?v={logoFileId:N}";
                }
            }
        }

        return await _emailService.SendSetPasswordEmailAsync(user, setPasswordUrl, logoUrl, accentColorHex, locationName);
    }

    public async Task<Result<bool>> DeleteUserAsync(string userId)
    {
        try
        {
            if (!await IsCurrentUserAdminOrManagerAsync())
            {
                return Result<bool>.Fail("You must be an administrator or manager to perform this action!");
            }

            UserProfile? user = await _userManager.FindByIdAsync(userId);

            if (user is null)
            {
                return Result<bool>.Fail("User not found!");
            }

            if (!await IsCurrentUserInRoleAsync("Admin") && await _userManager.IsInRoleAsync(user, "Admin"))
            {
                return Result<bool>.Fail("Only an administrator can perform this action on an administrator account.");
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
                // The user is already deleted at this point, so this is best-effort
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

    public async Task<Result<bool>> UnlockUserAsync(string userId)
    {
        try
        {
            if (!await IsCurrentUserAdminOrManagerAsync())
            {
                return Result<bool>.Fail("You must be an administrator or manager to perform this action!");
            }

            UserProfile? user = await _userManager.FindByIdAsync(userId);

            if (user is null)
            {
                return Result<bool>.Fail("User not found!");
            }

            if (!await IsCurrentUserInRoleAsync("Admin") && await _userManager.IsInRoleAsync(user, "Admin"))
            {
                return Result<bool>.Fail("Only an administrator can perform this action on an administrator account.");
            }

            // Clearing LockoutEnd alone leaves AccessFailedCount non-zero, so the next single
            // failed attempt would re-trigger MaxFailedAccessAttempts immediately - reset both.
            IdentityResult result = await _userManager.SetLockoutEndDateAsync(user, null);

            if (!result.Succeeded)
            {
                string errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return Result<bool>.Fail($"Failed to unlock user: {errors}");
            }

            IdentityResult resetResult = await _userManager.ResetAccessFailedCountAsync(user);

            if (!resetResult.Succeeded)
            {
                string errors = string.Join(", ", resetResult.Errors.Select(e => e.Description));
                return Result<bool>.Fail($"Failed to unlock user: {errors}");
            }

            _logger.LogInformation("User {UserId} was unlocked by {UnlockedByUserId}", user.Id, (await GetCurrentUserIdAsync()).Data);

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

    public async Task<Result<TwoFactorStatusDto>> GetTwoFactorStatusAsync()
    {
        try
        {
            UserProfile? user = await GetCurrentUserForTwoFactorAsync();

            if (user is null)
            {
                return Result<TwoFactorStatusDto>.Fail("User not found");
            }

            bool isEnabled = await _userManager.GetTwoFactorEnabledAsync(user);
            bool hasAuthenticator = !string.IsNullOrEmpty(await _userManager.GetAuthenticatorKeyAsync(user));
            int recoveryCodesRemaining = await _userManager.CountRecoveryCodesAsync(user);

            return Result<TwoFactorStatusDto>.Ok(new TwoFactorStatusDto
            {
                IsEnabled = isEnabled,
                HasAuthenticator = isEnabled && hasAuthenticator,
                RecoveryCodesRemaining = recoveryCodesRemaining
            });
        }
        catch (Exception ex)
        {
            return Result<TwoFactorStatusDto>.Fail(ex.Message);
        }
    }

    public async Task<Result<AuthenticatorEnrollmentDto>> BeginAuthenticatorEnrollmentAsync()
    {
        try
        {
            UserProfile? user = await GetCurrentUserForTwoFactorAsync();

            if (user is null)
            {
                return Result<AuthenticatorEnrollmentDto>.Fail("User not found");
            }

            await _userManager.ResetAuthenticatorKeyAsync(user);
            string? key = await _userManager.GetAuthenticatorKeyAsync(user);

            if (string.IsNullOrEmpty(key))
            {
                return Result<AuthenticatorEnrollmentDto>.Fail("Failed to generate an authenticator key.");
            }

            string accountName = user.Email ?? user.UserName ?? user.Id;
            string authenticatorUri = BuildAuthenticatorUri(accountName, key);

            return Result<AuthenticatorEnrollmentDto>.Ok(new AuthenticatorEnrollmentDto
            {
                SharedKey = FormatKeyForDisplay(key),
                AuthenticatorUri = authenticatorUri,
                QrCodeDataUri = BuildQrCodeDataUri(authenticatorUri)
            });
        }
        catch (Exception ex)
        {
            return Result<AuthenticatorEnrollmentDto>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Re-checks the signed-in user's second factor for a single sensitive action.
    ///
    /// This is a step-up check, not a login: the user is already authenticated, and the point is
    /// to prove the person at the keyboard is still them before something irreversible happens.
    /// Accepts an authenticator code, an emailed code, or a recovery code, matching what login
    /// accepts - somebody who has lost their phone should not also be locked out of changing
    /// where their takings land.
    /// </summary>
    public async Task<Result<bool>> VerifySecondFactorAsync(string code)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return Result<bool>.Fail("Enter your authentication code.");
            }

            UserProfile? user = await GetCurrentUserForTwoFactorAsync();

            if (user is null)
            {
                return Result<bool>.Fail("User not found");
            }

            // Checked before verifying anything: a locked-out account must not get another guess,
            // which is what makes counting failures below actually mean something.
            if (await _userManager.IsLockedOutAsync(user))
            {
                return Result<bool>.Fail("Too many attempts. Try again later.");
            }

            if (!await _userManager.GetTwoFactorEnabledAsync(user))
            {
                // Said plainly rather than waved through. Letting an account with no second
                // factor skip the check would make the whole requirement optional in practice.
                return Result<bool>.Fail(
                    "Set up two-factor authentication on your account before making this change.");
            }

            string trimmed = code.Trim();

            bool isValid = await _userManager.VerifyTwoFactorTokenAsync(
                user, TokenOptions.DefaultAuthenticatorProvider, trimmed);

            if (!isValid)
            {
                isValid = await _userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider, trimmed);
            }

            if (!isValid)
            {
                isValid = (await _userManager.RedeemTwoFactorRecoveryCodeAsync(user, trimmed)).Succeeded;
            }

            if (!isValid)
            {
                // Counted against lockout for the same reason login does: this endpoint would
                // otherwise be an unlimited oracle for guessing a six-digit code.
                await _userManager.AccessFailedAsync(user);

                _logger.LogWarning("User {UserId} failed a step-up two-factor check", user.Id);

                return Result<bool>.Fail("Invalid or expired code.");
            }

            await _userManager.ResetAccessFailedCountAsync(user);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<List<string>>> ConfirmAuthenticatorEnrollmentAsync(string code)
    {
        try
        {
            UserProfile? user = await GetCurrentUserForTwoFactorAsync();

            if (user is null)
            {
                return Result<List<string>>.Fail("User not found");
            }

            bool isValid = await _userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code);

            if (!isValid)
            {
                return Result<List<string>>.Fail("Invalid verification code.");
            }

            await _userManager.SetTwoFactorEnabledAsync(user, true);
            IEnumerable<string> recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 8) ?? [];

            _logger.LogInformation("User {UserId} enabled two-factor authentication via authenticator app", user.Id);

            return Result<List<string>>.Ok(recoveryCodes.ToList());
        }
        catch (Exception ex)
        {
            return Result<List<string>>.Fail(ex.Message);
        }
    }

    public async Task<Result<List<string>>> EnableEmailTwoFactorAsync()
    {
        try
        {
            UserProfile? user = await GetCurrentUserForTwoFactorAsync();

            if (user is null)
            {
                return Result<List<string>>.Fail("User not found");
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return Result<List<string>>.Fail("You need an email address on your account before enabling email codes.");
            }

            await _userManager.SetTwoFactorEnabledAsync(user, true);

            int remaining = await _userManager.CountRecoveryCodesAsync(user);
            List<string> recoveryCodes = [];

            if (remaining == 0)
            {
                IEnumerable<string> codes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 8) ?? [];
                recoveryCodes = codes.ToList();
            }

            _logger.LogInformation("User {UserId} enabled two-factor authentication via email codes", user.Id);

            return Result<List<string>>.Ok(recoveryCodes);
        }
        catch (Exception ex)
        {
            return Result<List<string>>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> DisableTwoFactorAsync(string currentPassword)
    {
        try
        {
            UserProfile? user = await GetCurrentUserForTwoFactorAsync();

            if (user is null)
            {
                return Result<bool>.Fail("User not found");
            }

            if (!await _userManager.CheckPasswordAsync(user, currentPassword))
            {
                return Result<bool>.Fail("Incorrect password.");
            }

            await _userManager.SetTwoFactorEnabledAsync(user, false);

            _logger.LogInformation("User {UserId} disabled two-factor authentication", user.Id);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<List<string>>> RegenerateRecoveryCodesAsync()
    {
        try
        {
            UserProfile? user = await GetCurrentUserForTwoFactorAsync();

            if (user is null)
            {
                return Result<List<string>>.Fail("User not found");
            }

            if (!await _userManager.GetTwoFactorEnabledAsync(user))
            {
                return Result<List<string>>.Fail("Two-factor authentication is not enabled.");
            }

            IEnumerable<string> codes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 8) ?? [];

            return Result<List<string>>.Ok(codes.ToList());
        }
        catch (Exception ex)
        {
            return Result<List<string>>.Fail(ex.Message);
        }
    }

    // UserManager.SetTwoFactorEnabledAsync/GenerateNewTwoFactorRecoveryCodesAsync/etc. attach the
    // passed-in entity to UserManager's own tracked DbContext. GetCurrentUserProfileAsync resolves
    // the user through a separate context (via IDbContextFactory), so passing that entity into the
    // UserManager here would throw an EF "already tracked" conflict - resolve via FindByIdAsync
    // instead so the entity belongs to the same context UserManager will mutate it through.
    private async Task<UserProfile?> GetCurrentUserForTwoFactorAsync()
    {
        Result<string> idResult = await GetCurrentUserIdAsync();

        if (!idResult.IsSuccess || idResult.Data is null)
        {
            return null;
        }

        return await _userManager.FindByIdAsync(idResult.Data);
    }

    private static string BuildAuthenticatorUri(string accountName, string unformattedKey)
    {
        const string issuer = "Lanyard";

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6",
            Uri.EscapeDataString(issuer),
            Uri.EscapeDataString(accountName),
            unformattedKey);
    }

    private static string BuildQrCodeDataUri(string authenticatorUri)
    {
        using QRCodeGenerator qrGenerator = new();
        using QRCodeData qrData = qrGenerator.CreateQrCode(authenticatorUri, QRCodeGenerator.ECCLevel.Q);
        PngByteQRCode qrCode = new(qrData);
        byte[] bytes = qrCode.GetGraphic(10);

        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
    }

    private static string FormatKeyForDisplay(string key)
    {
        StringBuilder sb = new();

        for (int i = 0; i < key.Length; i += 4)
        {
            sb.Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
            sb.Append(' ');
        }

        return sb.ToString().TrimEnd().ToUpperInvariant();
    }
}
