#nullable enable

using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services;

/// <summary>
/// The pure parts of the party-time trigger, kept out of AutomationEngineService so the timing
/// and wording can be tested at fixed instants.
/// </summary>
public static class PartyTriggerRules
{
    public const int MaxLeadMinutes = 120;

    /// <summary>The milestone's time for this party, or null when the party has no such time.</summary>
    public static DateTime? MilestoneUtc(PartySlot party, PartyMilestone milestone) => milestone switch
    {
        PartyMilestone.Start => party.StartUtc,
        PartyMilestone.EatTime => party.EatTimeUtc,
        PartyMilestone.LaserTag => party.LaserTagTimeUtc,
        PartyMilestone.End => party.EndUtc,
        _ => null
    };

    /// <summary>
    /// Due from <c>milestone - lead</c> until the catch-up window runs out. Like Scheduled rules, a
    /// tick that lands a little late still fires, but a restart hours later doesn't replay it.
    /// </summary>
    public static bool IsDue(DateTime milestoneUtc, int leadMinutes, DateTime nowUtc, TimeSpan catchUpWindow)
    {
        TimeSpan elapsed = nowUtc - milestoneUtc.AddMinutes(-leadMinutes);

        return elapsed >= TimeSpan.Zero && elapsed <= catchUpWindow;
    }

    /// <summary>
    /// The execution log's TriggerEvent, e.g. "Party: laser tag (10 min before) +1 other party".
    /// Deliberately has no child's name or room: the log keeps the party's id instead.
    /// </summary>
    public static string ExecutionLabel(PartyMilestone milestone, int leadMinutes, int otherParties)
    {
        string label = $"Party: {MilestoneName(milestone)}{(leadMinutes > 0 ? $" ({leadMinutes} min before)" : string.Empty)}";

        return otherParties switch
        {
            <= 0 => label,
            1 => $"{label} +1 other party",
            _ => $"{label} +{otherParties} other parties"
        };
    }

    /// <summary>"10 min before laser tag", "At party start".</summary>
    public static string Describe(PartyMilestone milestone, int leadMinutes)
    {
        string name = MilestoneName(milestone);

        return leadMinutes > 0 ? $"{leadMinutes} min before {name}" : $"At {name}";
    }

    public static string MilestoneName(PartyMilestone milestone) => milestone switch
    {
        PartyMilestone.Start => "party start",
        PartyMilestone.EatTime => "eat time",
        PartyMilestone.LaserTag => "laser tag",
        PartyMilestone.End => "party end",
        _ => "party time"
    };

    /// <summary>
    /// Null when the settings can fire; otherwise what's wrong. Public so the rule dialog gates its
    /// Next button on the same check the save applies.
    /// </summary>
    public static string? Validate(int? locationId, PartyMilestone? milestone, int? leadMinutes, string? room)
    {
        if (locationId is not > 0)
        {
            return "A party time rule needs a location.";
        }

        if (milestone is not PartyMilestone m || !System.Enum.IsDefined(m))
        {
            return "A party time rule needs to say which part of the party it's for.";
        }

        if (leadMinutes is not int lead || lead < 0 || lead > MaxLeadMinutes)
        {
            return $"Minutes before must be between 0 and {MaxLeadMinutes}.";
        }

        if (room is not null && room.Trim().Length > PartyRooms.MaxLength)
        {
            return $"The room can be at most {PartyRooms.MaxLength} characters.";
        }

        return null;
    }
}
