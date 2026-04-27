using System.Globalization;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Time;

public class TimeService : ITimeService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly ISecurityService _sApi;

    public TimeService(
        IDbContextFactory<ApplicationDbContext> factory,
        ISecurityService sApi)
    {
        _factory = factory;
        _sApi = sApi;
    }

    public async Task<Result<CultureInfo>> GetUserCultureAsync()
    {
        Result<UserProfile> userResult = await _sApi.GetCurrentUserProfileAsync();

        if (!userResult.IsSuccess)
        {
            return Result<CultureInfo>.Fail(userResult.Error!);
        }

        UserProfile user = userResult.Data!;
        if (string.IsNullOrEmpty(user.PreferredCulture))
        {
            return Result<CultureInfo>.Ok(CultureInfo.CurrentCulture);
        }

        try
        {
            CultureInfo culture = new CultureInfo(user.PreferredCulture);
            return Result<CultureInfo>.Ok(culture);
        }
        catch (CultureNotFoundException)
        {
            return Result<CultureInfo>.Fail($"Invalid culture code: {user.PreferredCulture}");
        }
    }

    public async Task<Result<bool>> SetUserCultureAsync(string cultureCode)
    {
        Result<UserProfile> userResult = await _sApi.GetCurrentUserProfileAsync();

        if (!userResult.IsSuccess)
        {
            return Result<bool>.Fail(userResult.Error!);
        }

        UserProfile user = userResult.Data!;

        try
        {
            CultureInfo culture = new CultureInfo(cultureCode);
            user.PreferredCulture = cultureCode;

            using ApplicationDbContext context = _factory.CreateDbContext();
            context.Users.Update(user);
            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (CultureNotFoundException)
        {
            return Result<bool>.Fail($"Invalid culture code: {cultureCode}");
        }
    }
}
