using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Themes;

public interface ISeasonalThemeService
{
    // The season the calendar says it is at the venue right now, ignoring every setting.
    SeasonalTheme ResolveForDate(DateTime utcNow);

    // What the staff app should show: the user's opt-out wins, then the company's setting
    // (Off, or a forced season for previewing), then the calendar.
    SeasonalTheme Resolve(SeasonalThemeSetting companySetting, bool userOptIn, DateTime utcNow);

    // Same, using the current time.
    SeasonalTheme Resolve(SeasonalThemeSetting companySetting, bool userOptIn);

    Task<Result<bool>> GetUserOptInAsync(string userId);

    Task<Result<bool>> SetUserOptInAsync(string userId, bool show);
}
