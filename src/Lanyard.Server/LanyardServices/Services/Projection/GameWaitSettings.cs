using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Projection;

/// <summary>
/// A "Wait for Game" step's settings, read from its parameter values. One parser for both the
/// editor's save validation and the runner, so a program that saved can always start.
/// </summary>
public sealed record GameWaitSettings(ProjectionGameEvent Event, Guid KioskClientId, int? SecondsLeft, int? GiveUpAfterMinutes)
{
    /// <summary>Null means wait for as long as it takes.</summary>
    public TimeSpan? Timeout => GiveUpAfterMinutes is > 0 ? TimeSpan.FromMinutes(GiveUpAfterMinutes.Value) : null;

    /// <param name="valuesByParameterName">The step's parameter values, keyed by parameter name.</param>
    public static Result<GameWaitSettings> Parse(IReadOnlyDictionary<string, string?> valuesByParameterName)
    {
        string? eventText = Get(valuesByParameterName, GameWaitStepNames.GameEventParameter);

        if (string.IsNullOrWhiteSpace(eventText))
        {
            return Result<GameWaitSettings>.Fail("Choose which game event to wait for.");
        }

        // Defined names only: Enum.TryParse would also take "2" or "GetReady, GameEnds".
        if (!System.Enum.TryParse(eventText.Trim(), ignoreCase: false, out ProjectionGameEvent gameEvent)
            || !System.Enum.IsDefined(gameEvent)
            || eventText.Trim() != gameEvent.ToString())
        {
            return Result<GameWaitSettings>.Fail($"\"{eventText}\" isn't a game event a step can wait for.");
        }

        string? kioskText = Get(valuesByParameterName, GameWaitStepNames.LaserKioskParameter);

        if (string.IsNullOrWhiteSpace(kioskText))
        {
            return Result<GameWaitSettings>.Fail("Choose the laser kiosk whose game to wait for.");
        }

        if (!Guid.TryParse(kioskText.Trim(), out Guid kioskClientId) || kioskClientId == Guid.Empty)
        {
            return Result<GameWaitSettings>.Fail("The laser kiosk to wait for isn't valid. Choose it again.");
        }

        int? secondsLeft = null;

        if (gameEvent == ProjectionGameEvent.TimeLeftBelow)
        {
            string? secondsText = Get(valuesByParameterName, GameWaitStepNames.SecondsLeftParameter);

            if (string.IsNullOrWhiteSpace(secondsText)
                || !int.TryParse(secondsText.Trim(), out int seconds)
                || seconds < GameWaitStepNames.MinSecondsLeft
                || seconds > GameWaitStepNames.MaxSecondsLeft)
            {
                return Result<GameWaitSettings>.Fail($"Seconds Left must be a whole number from {GameWaitStepNames.MinSecondsLeft} to {GameWaitStepNames.MaxSecondsLeft}.");
            }

            secondsLeft = seconds;
        }

        int? giveUpAfter = null;
        string? giveUpText = Get(valuesByParameterName, GameWaitStepNames.GiveUpAfterParameter);

        if (!string.IsNullOrWhiteSpace(giveUpText))
        {
            if (!int.TryParse(giveUpText.Trim(), out int minutes) || minutes < 0 || minutes > GameWaitStepNames.MaxGiveUpAfterMinutes)
            {
                return Result<GameWaitSettings>.Fail($"Give Up After must be a whole number of minutes from 0 to {GameWaitStepNames.MaxGiveUpAfterMinutes} (0 or blank waits for as long as it takes).");
            }

            giveUpAfter = minutes == 0 ? null : minutes;
        }

        return Result<GameWaitSettings>.Ok(new GameWaitSettings(gameEvent, kioskClientId, secondsLeft, giveUpAfter));
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out string? value) ? value : null;
}
