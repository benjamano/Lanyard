using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Projection;

/// <summary>
/// The "Wait for Game" step template is matched by name, like every built-in template (see
/// Lanyard.Shared's ProjectionTemplateNames). Its names live server-side because only the server
/// runs it: the kiosk's offline player never sees the step.
/// </summary>
public static class GameWaitStepNames
{
    public const string Template = "Wait for Game";

    public const string GameEventParameter = "Game Event";
    public const string LaserKioskParameter = "Laser Kiosk";
    public const string SecondsLeftParameter = "Seconds Left";
    public const string GiveUpAfterParameter = "Give Up After (minutes)";

    // Parameter data types the step editor gives their own inputs.
    public const string GameEventDataType = "GameEvent";
    public const string LaserKioskDataType = "LaserKiosk";

    public const int MinSecondsLeft = 1;
    public const int MaxSecondsLeft = 3600;
    public const int MaxGiveUpAfterMinutes = 240;

    public static bool IsWaitForGame(string? templateName) =>
        string.Equals(templateName, Template, StringComparison.Ordinal);

    /// <summary>The choices the step editor offers, in order.</summary>
    public static IReadOnlyList<ProjectionGameEvent> Events { get; } =
        [ProjectionGameEvent.GetReady, ProjectionGameEvent.GameStarts, ProjectionGameEvent.GameEnds, ProjectionGameEvent.TimeLeftBelow];

    public static string EventLabel(ProjectionGameEvent gameEvent) => gameEvent switch
    {
        ProjectionGameEvent.GetReady => "Get ready",
        ProjectionGameEvent.GameStarts => "Game starts",
        ProjectionGameEvent.GameEnds => "Game ends",
        ProjectionGameEvent.TimeLeftBelow => "Time left drops below",
        _ => gameEvent.ToString()
    };

    /// <summary>Reads as "Waiting for {this}", e.g. "the game to end".</summary>
    public static string WaitingFor(ProjectionGameEvent gameEvent, int? secondsLeft) => gameEvent switch
    {
        ProjectionGameEvent.GetReady => "get ready",
        ProjectionGameEvent.GameStarts => "the game to start",
        ProjectionGameEvent.GameEnds => "the game to end",
        ProjectionGameEvent.TimeLeftBelow => $"{FormatSeconds(secondsLeft ?? 0)} left in the game",
        _ => gameEvent.ToString()
    };

    public static string FormatSeconds(int seconds) => seconds switch
    {
        < 60 => seconds == 1 ? "1 second" : $"{seconds} seconds",
        _ when seconds % 60 == 0 => seconds == 60 ? "1 minute" : $"{seconds / 60} minutes",
        _ => $"{seconds / 60}:{seconds % 60:00}"
    };
}
