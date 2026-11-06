using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Themes;

public class SeasonalThemeService(
    IDbContextFactory<ApplicationDbContext> factory,
    TimeProvider timeProvider,
    ILogger<SeasonalThemeService> logger) : ISeasonalThemeService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<SeasonalThemeService> _logger = logger;

    // Inclusive month/day windows in venue-local time. A new season is one line here plus its
    // CSS/JS; windows must not cross the new year (none do yet).
    private static readonly (SeasonalTheme Theme, int StartMonth, int StartDay, int EndMonth, int EndDay)[] Windows =
    [
        (SeasonalTheme.Halloween, 10, 24, 10, 31),
        (SeasonalTheme.Christmas, 12, 1, 12, 26),
    ];

    public SeasonalTheme ResolveForDate(DateTime utcNow)
    {
        DateOnly today = RotaTime.LocalDate(utcNow);

        foreach ((SeasonalTheme theme, int startMonth, int startDay, int endMonth, int endDay) in Windows)
        {
            DateOnly start = new(today.Year, startMonth, startDay);
            DateOnly end = new(today.Year, endMonth, endDay);

            if (today >= start && today <= end)
            {
                return theme;
            }
        }

        return SeasonalTheme.None;
    }

    public SeasonalTheme Resolve(SeasonalThemeSetting companySetting, bool userOptIn, DateTime utcNow)
    {
        if (!userOptIn)
        {
            return SeasonalTheme.None;
        }

        return companySetting switch
        {
            SeasonalThemeSetting.Off => SeasonalTheme.None,
            SeasonalThemeSetting.Christmas => SeasonalTheme.Christmas,
            SeasonalThemeSetting.Halloween => SeasonalTheme.Halloween,
            _ => ResolveForDate(utcNow),
        };
    }

    public SeasonalTheme Resolve(SeasonalThemeSetting companySetting, bool userOptIn) =>
        Resolve(companySetting, userOptIn, _timeProvider.GetUtcNow().UtcDateTime);

    public async Task<Result<bool>> GetUserOptInAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            bool? show = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == userId)
                .Select(x => (bool?)x.ShowSeasonalThemes)
                .FirstOrDefaultAsync();

            return show is bool value ? Result<bool>.Ok(value) : Result<bool>.Fail("Account not found.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading the seasonal-theme setting for {UserId}", userId);

            return Result<bool>.Fail($"Couldn't load that setting: {ex.Message}");
        }
    }

    public async Task<Result<bool>> SetUserOptInAsync(string userId, bool show)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            UserProfile? user = await ctx.Users.FirstOrDefaultAsync(x => x.Id == userId);

            if (user is null)
            {
                return Result<bool>.Fail("Account not found.");
            }

            user.ShowSeasonalThemes = show;
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(show);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving the seasonal-theme setting for {UserId}", userId);

            return Result<bool>.Fail($"Couldn't save that setting: {ex.Message}");
        }
    }
}
