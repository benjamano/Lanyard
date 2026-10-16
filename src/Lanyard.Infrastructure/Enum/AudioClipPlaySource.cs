namespace Lanyard.Infrastructure.Enum;

// Stored, so never renumber.
/// <summary>What asked for an audio clip to play.</summary>
public enum AudioClipPlaySource
{
    Manual = 1,
    Schedule = 2,
    Automation = 3
}
