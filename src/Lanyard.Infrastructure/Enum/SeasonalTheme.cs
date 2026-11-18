namespace Lanyard.Infrastructure.Enum;

// The decoration the staff app is showing right now. Resolved per request from the company's
// SeasonalThemeSetting, the user's opt-out and the venue-local date.
public enum SeasonalTheme
{
    None = 0,
    Christmas = 1,
    Halloween = 2,
}

// Company-wide choice made by a manager. Automatic follows the calendar; the named seasons force
// that decoration on regardless of the date, which is how managers preview them.
public enum SeasonalThemeSetting
{
    Automatic = 0,
    Off = 1,
    Christmas = 2,
    Halloween = 3,
}
