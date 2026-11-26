namespace Lanyard.Infrastructure.Enum;

// Stored (on voice presets), so never renumber.
/// <summary>Which text-to-speech engine speaks a voice.</summary>
public enum SpeechEngineKind
{
    /// <summary>Piper neural voices, run inside the Lanyard server: free, and no text leaves it.</summary>
    Piper = 1
}
