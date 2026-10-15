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
    Scheduled = 2,

    /// <summary>
    /// Fires once per game when the trigger client's countdown passes
    /// <see cref="Lanyard.Infrastructure.Models.AutomationRule.TriggerThreshold"/> seconds. Needs a
    /// reading above the threshold earlier in the same game, so a game shorter than the threshold,
    /// or a kiosk that reports 0 before its first countdown packet, never fires it.
    /// </summary>
    TimeRemainingBelow = 3,

    /// <summary>
    /// Fires once per game when a score on the trigger client reaches
    /// <see cref="Lanyard.Infrastructure.Models.AutomationRule.TriggerThreshold"/>: any one player's,
    /// or a team's total, per <see cref="Lanyard.Infrastructure.Models.AutomationRule.TriggerScoreScope"/>,
    /// optionally limited to <see cref="Lanyard.Infrastructure.Models.AutomationRule.TriggerTeam"/>.
    /// </summary>
    ScoreThreshold = 4,

    /// <summary>
    /// Fires when a game on the trigger client ends with one team ahead on total score, for any
    /// team or only <see cref="Lanyard.Infrastructure.Models.AutomationRule.TriggerTeam"/>. A draw,
    /// a solo game or a game with only one team doesn't fire it.
    /// </summary>
    GameEnded = 5
}
