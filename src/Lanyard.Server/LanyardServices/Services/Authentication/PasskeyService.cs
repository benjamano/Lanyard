using System.Buffers.Text;
using Lanyard.Application.Services.Demo;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Authentication;

public class PasskeyService(
    UserManager<UserProfile> userManager,
    ICurrentUserAccessor currentUserAccessor,
    ILogger<PasskeyService> logger,
    IDemoGuard? demoGuard = null) : IPasskeyService
{
    // A person has a phone, maybe a tablet and a laptop. The cap stops a script filling the
    // table with credentials for one account (see "Resource limits" in the ASP.NET passkey docs).
    public const int MaxPasskeysPerUser = 10;

    public const string LimitReachedMessage = "You already have the maximum number of passkeys. Remove one before adding another.";

    public async Task<Result<bool>> CanAddPasskeyAsync(string userId)
    {
        // Everyone shares the demo accounts, so a passkey added there would sign the next
        // visitor's device in as someone else's session.
        if (demoGuard is not null && await demoGuard.IsDemoSessionAsync())
        {
            return Result<bool>.Fail(DemoGuard.NotInDemoMessage);
        }

        try
        {
            UserProfile? user = await userManager.FindByIdAsync(userId);

            if (user is null)
            {
                return Result<bool>.Fail("User not found");
            }

            IList<UserPasskeyInfo> passkeys = await userManager.GetPasskeysAsync(user);

            if (passkeys.Count >= MaxPasskeysPerUser)
            {
                return Result<bool>.Fail(LimitReachedMessage);
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Couldn't check your passkeys: {ex.Message}");
        }
    }

    public async Task<Result<string>> SavePasskeyAsync(string userId, UserPasskeyInfo passkey, string? userAgent)
    {
        Result<bool> canAdd = await CanAddPasskeyAsync(userId);

        if (!canAdd.IsSuccess)
        {
            return Result<string>.Fail(canAdd.Error!);
        }

        try
        {
            UserProfile? user = await userManager.FindByIdAsync(userId);

            if (user is null)
            {
                return Result<string>.Fail("User not found");
            }

            passkey.Name = NameForDevice(userAgent);

            IdentityResult result = await userManager.AddOrUpdatePasskeyAsync(user, passkey);

            if (!result.Succeeded)
            {
                return Result<string>.Fail(string.Join(" ", result.Errors.Select(x => x.Description)));
            }

            logger.LogInformation("Added passkey {PasskeyName} for {UserId}", passkey.Name, userId);

            return Result<string>.Ok(passkey.Name);
        }
        catch (Exception ex)
        {
            return Result<string>.Fail($"Couldn't save the passkey: {ex.Message}");
        }
    }

    public async Task<Result<List<PasskeySummaryDto>>> GetMyPasskeysAsync()
    {
        try
        {
            UserProfile? user = await GetCurrentUserAsync();

            if (user is null)
            {
                return Result<List<PasskeySummaryDto>>.Fail("User not found");
            }

            IList<UserPasskeyInfo> passkeys = await userManager.GetPasskeysAsync(user);

            List<PasskeySummaryDto> summaries = passkeys
                .OrderBy(x => x.CreatedAt)
                .Select(x => new PasskeySummaryDto(
                    Base64Url.EncodeToString(x.CredentialId),
                    string.IsNullOrWhiteSpace(x.Name) ? "Passkey" : x.Name,
                    x.CreatedAt,
                    x.IsBackedUp))
                .ToList();

            return Result<List<PasskeySummaryDto>>.Ok(summaries);
        }
        catch (Exception ex)
        {
            return Result<List<PasskeySummaryDto>>.Fail($"Couldn't load your passkeys: {ex.Message}");
        }
    }

    public async Task<Result<bool>> RemoveMyPasskeyAsync(string passkeyId)
    {
        if (demoGuard is not null && await demoGuard.IsDemoSessionAsync())
        {
            return Result<bool>.Fail(DemoGuard.NotInDemoMessage);
        }

        byte[] credentialId;

        try
        {
            credentialId = Base64Url.DecodeFromChars(passkeyId);
        }
        catch (FormatException)
        {
            return Result<bool>.Fail("Passkey not found");
        }

        try
        {
            UserProfile? user = await GetCurrentUserAsync();

            if (user is null)
            {
                return Result<bool>.Fail("User not found");
            }

            // Scoped to the current user: RemovePasskeyAsync only deletes a credential belonging
            // to the user it's given, so another account's passkey id just isn't found.
            if (await userManager.GetPasskeyAsync(user, credentialId) is null)
            {
                return Result<bool>.Fail("Passkey not found");
            }

            IdentityResult result = await userManager.RemovePasskeyAsync(user, credentialId);

            if (!result.Succeeded)
            {
                return Result<bool>.Fail(string.Join(" ", result.Errors.Select(x => x.Description)));
            }

            logger.LogInformation("Removed a passkey for {UserId}", user.Id);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Couldn't remove the passkey: {ex.Message}");
        }
    }

    // Passkeys are listed by the device they were made on, so someone can tell which one to
    // remove when they replace a phone. Only a label - nothing relies on it.
    public static string NameForDevice(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return "Passkey";
        }

        // Order matters: iPadOS Safari and Android both also claim to be other platforms.
        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
        {
            return "iPhone";
        }

        if (userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase))
        {
            return "iPad";
        }

        if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return "Android device";
        }

        if (userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase))
        {
            return "Mac";
        }

        if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows PC";
        }

        if (userAgent.Contains("CrOS", StringComparison.OrdinalIgnoreCase))
        {
            return "Chromebook";
        }

        if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase))
        {
            return "Linux PC";
        }

        return "Passkey";
    }

    private async Task<UserProfile?> GetCurrentUserAsync()
    {
        Result<string> idResult = await currentUserAccessor.GetCurrentUserIdAsync();

        if (!idResult.IsSuccess || idResult.Data is null)
        {
            return null;
        }

        return await userManager.FindByIdAsync(idResult.Data);
    }
}
