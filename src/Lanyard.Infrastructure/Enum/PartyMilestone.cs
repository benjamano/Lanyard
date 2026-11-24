namespace Lanyard.Infrastructure.Enum;

/// <summary>
/// The moment in a party an automation rule fires at. Stored as an int on AutomationRule, so
/// existing values must never be renumbered.
/// </summary>
public enum PartyMilestone
{
    /// <summary>The party's arrival time (PartyBooking.StartUtc).</summary>
    Start = 0,

    /// <summary>The eating time (PartyBooking.EatTimeUtc). A party without one never fires it.</summary>
    EatTime = 1,

    /// <summary>The laser tag time (PartyBooking.LaserTagTimeUtc). A party without one never fires it.</summary>
    LaserTag = 2,

    /// <summary>The party's finish time (PartyBooking.EndUtc).</summary>
    End = 3
}
