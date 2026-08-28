namespace Lanyard.Infrastructure.Enum;

public enum AutomationTriggerType
{
    /// <summary>
    /// Fires on a game status edge for the trigger client, e.g. NotStarted -> InGame.
    /// </summary>
    /// <remarks>
    /// Must stay the zero value: every rule that existed before idle triggers were added migrates
    /// into this member by default.
    /// </remarks>
    GameStatusTransition = 0,

    /// <summary>
    /// Fires once when the trigger client has sat at the same game status for
    /// <see cref="Lanyard.Infrastructure.Models.AutomationRule.IdleThresholdMinutes"/> minutes.
    /// TriggerEvent is ignored for this type.
    /// </summary>
    ClientIdle = 1
}
