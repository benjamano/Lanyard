namespace Lanyard.Application.Services.Speech;

/// <summary>The "Speech" configuration section.</summary>
public sealed class SpeechOptions
{
    public const string Section = "Speech";

    /// <summary>
    /// The folder holding the Piper voices (one sub-folder per voice, plus espeak-ng-data). Left
    /// blank, the server looks in "voices" next to the app (where the Docker image puts them),
    /// then in %AppData%/Lanyard/Voices (where scripts/download-voices.sh puts them for local runs).
    /// </summary>
    public string? VoicesPath { get; set; }

    /// <summary>How many voices may be loaded at once. Each takes about 70 MB of memory.</summary>
    public int MaxLoadedVoices { get; set; } = 2;

    /// <summary>A voice nobody has used for this long is unloaded to give its memory back.</summary>
    public int IdleUnloadMinutes { get; set; } = 10;

    public int NumThreads { get; set; } = 2;
}
