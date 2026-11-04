namespace Lanyard.Application.Services;

/// <summary>
/// Who asked for a music command. Anything that isn't the schedule (a person on the Music page or
/// a dashboard widget, or an automation rule) counts as a change by hand, which holds the music
/// schedule off until its next part of the day starts.
/// </summary>
public enum MusicCommandSource
{
    Manual,
    Schedule
}
