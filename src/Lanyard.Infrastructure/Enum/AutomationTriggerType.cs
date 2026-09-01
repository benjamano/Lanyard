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
    ClientIdle = 1,

    /// <summary>
    /// Fires once per matching local calendar day at
    /// <see cref="Lanyard.Infrastructure.Models.AutomationRule.ScheduledTimeOfDay"/>, on the days
    /// listed in <see cref="Lanyard.Infrastructure.Models.AutomationRule.ScheduledDaysOfWeek"/>
    /// (or every day if that's null/empty). TriggerEvent and IdleThresholdMinutes are ignored for
    /// this type. TriggerClientId is still required by the schema but has no effect on execution -
    /// it is only an audit-log label.
    /// </summary>
    Scheduled = 2
}
