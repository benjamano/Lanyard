using Lanyard.Application.Services.Speech;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;

namespace Lanyard.Tests.Services.Speech;

// "Speaks" a 220 Hz tone, 60 ms per character at normal speed, so lengths and pitches can be checked.
internal sealed class FakeSpeechEngine : ISpeechEngine
{
    public const int SampleRate = 16000;

    public List<(string Text, string VoiceId, double Speed)> Calls { get; } = [];

    public bool Installed { get; set; } = true;

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public SpeechEngineKind Kind => SpeechEngineKind.Piper;

    public bool IsInstalled(SpeechVoice voice) => Installed;

    public async Task<Result<SpeechSamples>> SynthesiseAsync(string text, SpeechVoice voice, double speed, CancellationToken ct)
    {
        Calls.Add((text, voice.Id, speed));

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        int length = (int)(text.Length * 0.06 * SampleRate / speed);
        float[] samples = new float[length];

        for (int i = 0; i < length; i++)
        {
            samples[i] = 0.3f * (float)Math.Sin(2 * Math.PI * 220 * i / SampleRate);
        }

        return Result<SpeechSamples>.Ok(new SpeechSamples(samples, SampleRate));
    }
}
