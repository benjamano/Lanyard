namespace Lanyard.Infrastructure.Enum;

// Stored, so never renumber.
/// <summary>Where a clip's audio came from.</summary>
public enum AudioClipKind
{
    /// <summary>An uploaded recording, or a file already in the file manager.</summary>
    File = 1,

    /// <summary>
    /// Text typed into Lanyard and spoken by a voice. The spoken audio is rendered on the server
    /// and stored as the clip's file, so it plays exactly like an uploaded one.
    /// </summary>
    Speech = 2
}
