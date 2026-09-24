using System.Text.RegularExpressions;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

// Uses Identity's PasswordHasher (salted PBKDF2) rather than a plain digest: the terminal knows
// which user is typing, so the hash never needs to be searchable, and a salted slow hash means a
// leaked table can't be reversed into everyone's 4-digit PIN with a 10,000-entry lookup.
public partial class ClockInPinService(
    IDbContextFactory<ApplicationDbContext> factory,
    IPasswordHasher<UserProfile> passwordHasher) : IClockInPinService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IPasswordHasher<UserProfile> _passwordHasher = passwordHasher;

    [GeneratedRegex(@"^\d{4,6}$")]
    private static partial Regex PinFormat();

    public Task<Result<bool>> SetOwnPinAsync(string userId, string pin) =>
        SetPinCoreAsync(null, userId, pin, null);

    public Task<Result<bool>> ClearOwnPinAsync(string userId) =>
        ClearPinCoreAsync(null, userId);

    public Task<Result<bool>> SetPinForUserAsync(LocationScope scope, string userId, string pin, string setByUserId) =>
        SetPinCoreAsync(scope, userId, pin, setByUserId);

    public Task<Result<bool>> ClearPinForUserAsync(LocationScope scope, string userId) =>
        ClearPinCoreAsync(scope, userId);

    private async Task<Result<bool>> SetPinCoreAsync(LocationScope? scope, string userId, string pin, string? setByUserId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pin) || !PinFormat().IsMatch(pin))
            {
                return Result<bool>.Fail("A PIN must be 4 to 6 digits.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (scope is not null && !await SchedulingAccess.CanManageUserAsync(ctx, scope, userId))
            {
                return Result<bool>.Fail("You can only set PINs for staff in your own company.");
            }

            bool userExists = await ctx.Users.AnyAsync(x => x.Id == userId);

            if (!userExists)
            {
                return Result<bool>.Fail("User not found.");
            }

            UserClockInPin? existing = await ctx.UserClockInPins.FirstOrDefaultAsync(x => x.UserId == userId);

            string hash = _passwordHasher.HashPassword(new UserProfile { Id = userId }, pin);

            if (existing is null)
            {
                ctx.UserClockInPins.Add(new UserClockInPin
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    PinHash = hash,
                    SetDate = DateTime.UtcNow,
                    SetByUserId = setByUserId
                });
            }
            else
            {
                existing.PinHash = hash;
                existing.SetDate = DateTime.UtcNow;
                existing.SetByUserId = setByUserId;
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to set the PIN: {ex.Message}");
        }
    }

    private async Task<Result<bool>> ClearPinCoreAsync(LocationScope? scope, string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (scope is not null && !await SchedulingAccess.CanManageUserAsync(ctx, scope, userId))
            {
                return Result<bool>.Fail("You can only remove PINs for staff in your own company.");
            }

            UserClockInPin? existing = await ctx.UserClockInPins.FirstOrDefaultAsync(x => x.UserId == userId);

            if (existing is not null)
            {
                ctx.UserClockInPins.Remove(existing);
                await ctx.SaveChangesAsync();
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to clear the PIN: {ex.Message}");
        }
    }

    public async Task<Result<ClockInPinStatus>> GetStatusAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            DateTime? setDate = await ctx.UserClockInPins
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == userId)
                .Select(x => (DateTime?)x.SetDate)
                .FirstOrDefaultAsync();

            return Result<ClockInPinStatus>.Ok(new ClockInPinStatus(setDate is not null, setDate));
        }
        catch (Exception ex)
        {
            return Result<ClockInPinStatus>.Fail($"Failed to check the PIN: {ex.Message}");
        }
    }

    public async Task<Result<bool>> VerifyPinAsync(string userId, string pin)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pin))
            {
                return Result<bool>.Ok(false);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? hash = await ctx.UserClockInPins
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.UserId == userId)
                .Select(x => x.PinHash)
                .FirstOrDefaultAsync();

            if (hash is null)
            {
                return Result<bool>.Ok(false);
            }

            PasswordVerificationResult verification = _passwordHasher.VerifyHashedPassword(new UserProfile { Id = userId }, hash, pin);

            return Result<bool>.Ok(verification != PasswordVerificationResult.Failed);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to verify the PIN: {ex.Message}");
        }
    }
}
