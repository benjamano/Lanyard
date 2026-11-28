namespace Lanyard.Application.Services.Speech;

/// <summary>The limits on what can be spoken, shared by the services and the dialogs.</summary>
public static class SpeechRules
{
    public const int MaxTextLength = Infrastructure.Models.AudioClip.SpeechTextMaxLength;

    public const double MinRate = 0.5;
    public const double MaxRate = 2.0;

    public const int MinPitch = -12;
    public const int MaxPitch = 12;

    // Silence around the words: the music is still fading down when a clip starts.
    public const double LeadSeconds = 0.15;
    public const double TailSeconds = 0.35;
}

/// <summary>How a voice should speak: which voice, how fast, and how high.</summary>
public sealed record SpeechVoiceSettings(string VoiceId, double Rate = 1.0, int Pitch = 0);

/// <summary>A rendered announcement: a mono 16-bit WAV file.</summary>
public sealed record RenderedSpeech(byte[] Wav, double DurationSeconds, string RenderKey);

/// <summary>A voice as the voice picker shows it.</summary>
public sealed record SpeechVoiceOption(string Id, string Name, string Description, bool IsInstalled);
