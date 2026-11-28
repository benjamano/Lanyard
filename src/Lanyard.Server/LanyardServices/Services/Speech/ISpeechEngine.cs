using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Speech;

/// <summary>Raw speech from an engine: mono floats from −1 to 1.</summary>
public sealed record SpeechSamples(float[] Samples, int SampleRate);

/// <summary>
/// A text-to-speech engine. Piper today; a cloud engine would be another implementation, picked
/// by the voice's <see cref="SpeechVoice.Engine"/>.
/// </summary>
public interface ISpeechEngine
{
    SpeechEngineKind Kind { get; }

    bool IsInstalled(SpeechVoice voice);

    /// <param name="speed">1 is the voice's normal pace; 2 says it in half the time.</param>
    Task<Result<SpeechSamples>> SynthesiseAsync(string text, SpeechVoice voice, double speed, CancellationToken ct);
}
