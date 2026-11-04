namespace Lanyard.Infrastructure.Enum;

// Stored, so never renumber.
/// <summary>What an audio clip schedule's time is measured from.</summary>
public enum AudioClipScheduleAnchor
{
    // A set time of day, such as 12:00.
    AtTime = 1,
    // A number of minutes before the location closes.
    BeforeClosing = 2,
    // A number of minutes after the location opens.
    AfterOpening = 3
}
