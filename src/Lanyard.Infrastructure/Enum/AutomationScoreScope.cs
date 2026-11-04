namespace Lanyard.Infrastructure.Enum;

/// <summary>Whose score a ScoreThreshold rule watches. Stored as an int, so never renumber.</summary>
public enum AutomationScoreScope
{
    /// <summary>Any single player's score.</summary>
    AnyPlayer = 0,

    /// <summary>The sum of one team's players' scores.</summary>
    Team = 1
}
