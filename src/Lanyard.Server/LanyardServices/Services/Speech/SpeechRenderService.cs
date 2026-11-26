using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lanyard.Infrastructure.DTO;
using Lanyard.Shared.Audio;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Speech;

/// <summary>
/// Turns text into an announcement: the engine speaks it, then the pitch, loudness and padding
/// are applied and it's written as a WAV file.
/// </summary>
public interface ISpeechRenderService
{
    IReadOnlyList<SpeechVoiceOption> ListVoices();

    /// <summary>Null when the text and voice are fine to render, otherwise why not.</summary>
    string? Validate(string? text, SpeechVoiceSettings voice);

    /// <summary>
    /// Identifies what a render sounds like. The same text and settings always give the same key,
    /// so a clip is only rendered again when something that changes the sound has changed.
    /// </summary>
    string RenderKeyFor(string text, SpeechVoiceSettings voice);

    Task<Result<RenderedSpeech>> RenderAsync(string text, SpeechVoiceSettings voice, CancellationToken ct = default);
}

public sealed class SpeechRenderService(
    IEnumerable<ISpeechEngine> engines,
    IMemoryCache cache,
    ILogger<SpeechRenderService> logger) : ISpeechRenderService
{
    // Loading a voice for the first time takes a couple of seconds; speaking a long announcement
    // a few more. Past this, something is wrong.
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);

    // Kept briefly so pressing Preview and then Save doesn't speak the same words twice. Memory
    // only: text can name people.
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly IReadOnlyList<ISpeechEngine> _engines = engines.ToList();

    public IReadOnlyList<SpeechVoiceOption> ListVoices() =>
        SpeechVoiceCatalogue.Voices
            .Select(v => new SpeechVoiceOption(v.Id, v.Name, v.Description, EngineFor(v)?.IsInstalled(v) == true))
            .ToList();

    public string? Validate(string? text, SpeechVoiceSettings voice)
    {
        string cleaned = Clean(text);

        if (cleaned.Length == 0)
        {
            return "Type what the announcement should say.";
        }

        if (cleaned.Length > SpeechRules.MaxTextLength)
        {
            return $"Announcements can be at most {SpeechRules.MaxTextLength} characters.";
        }

        if (!cleaned.Any(char.IsLetterOrDigit))
        {
            return "Type some words for the announcement to say.";
        }

        if (SpeechVoiceCatalogue.Find(voice.VoiceId) is null)
        {
            return "Choose a voice.";
        }

        if (voice.Rate is < SpeechRules.MinRate or > SpeechRules.MaxRate || double.IsNaN(voice.Rate))
        {
            return $"Speed goes from {SpeechRules.MinRate:0.#}× to {SpeechRules.MaxRate:0.#}×.";
        }

        if (voice.Pitch is < SpeechRules.MinPitch or > SpeechRules.MaxPitch)
        {
            return $"Pitch goes from {SpeechRules.MinPitch} to +{SpeechRules.MaxPitch}.";
        }

        return null;
    }

    public string RenderKeyFor(string text, SpeechVoiceSettings voice)
    {
        // "v1": bump it when the processing changes, so every clip is rendered again.
        string identity = string.Join('|',
            "v1",
            voice.VoiceId,
            Math.Round(voice.Rate, 2).ToString("0.00", CultureInfo.InvariantCulture),
            voice.Pitch.ToString(CultureInfo.InvariantCulture),
            Clean(text));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public async Task<Result<RenderedSpeech>> RenderAsync(string text, SpeechVoiceSettings voice, CancellationToken ct = default)
    {
        try
        {
            string? error = Validate(text, voice);
            if (error is not null)
            {
                return Result<RenderedSpeech>.Fail(error);
            }

            string key = RenderKeyFor(text, voice);

            if (cache.TryGetValue(CacheKey(key), out RenderedSpeech? cached) && cached is not null)
            {
                return Result<RenderedSpeech>.Ok(cached);
            }

            SpeechVoice speechVoice = SpeechVoiceCatalogue.Find(voice.VoiceId)!;
            ISpeechEngine? engine = EngineFor(speechVoice);

            if (engine is null || !engine.IsInstalled(speechVoice))
            {
                return Result<RenderedSpeech>.Fail($"The voice {speechVoice.Name} isn't installed on this server yet.");
            }

            // The pitch is moved afterwards by stretching the audio, which also changes its
            // length; the engine speaks faster or slower by the opposite amount to make up for it.
            double stretch = SpeechAudio.PitchFactor(voice.Pitch);
            double speed = voice.Rate * stretch;

            Result<SpeechSamples> spoken = await engine.SynthesiseAsync(Clean(text), speechVoice, speed, ct).WaitAsync(RenderTimeout, ct);

            if (!spoken.IsSuccess || spoken.Data is null)
            {
                return Result<RenderedSpeech>.Fail(spoken.Error ?? "The voice couldn't speak that.");
            }

            if (spoken.Data.Samples.Length == 0 || spoken.Data.SampleRate <= 0)
            {
                return Result<RenderedSpeech>.Fail("The voice didn't say anything. Try different words.");
            }

            float[] samples = SpeechAudio.Stretch(spoken.Data.Samples, stretch);
            SpeechAudio.Normalise(samples);
            samples = SpeechAudio.Pad(samples, spoken.Data.SampleRate, SpeechRules.LeadSeconds, SpeechRules.TailSeconds);

            RenderedSpeech rendered = new(
                SpeechAudio.ToWav(samples, spoken.Data.SampleRate),
                Math.Round(samples.Length / (double)spoken.Data.SampleRate, 1),
                key);

            cache.Set(CacheKey(key), rendered, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheFor, Size = 1 });

            logger.LogInformation("Rendered {Characters} characters of speech with voice {VoiceId} ({Seconds}s)", Clean(text).Length, voice.VoiceId, rendered.DurationSeconds);

            return Result<RenderedSpeech>.Ok(rendered);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Rendering speech with voice {VoiceId} took longer than {Timeout}", voice.VoiceId, RenderTimeout);
            return Result<RenderedSpeech>.Fail("The voice took too long to speak. Try again in a moment.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<RenderedSpeech>.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to render speech with voice {VoiceId}", voice.VoiceId);
            return Result<RenderedSpeech>.Fail($"Failed to make the announcement: {ex.Message}");
        }
    }

    /// <summary>
    /// Control characters become spaces and runs of whitespace collapse, so what's stored, what's
    /// hashed and what's spoken are the same text.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        StringBuilder builder = new(text.Length);
        bool lastWasSpace = false;

        foreach (char c in text)
        {
            bool space = char.IsWhiteSpace(c) || char.IsControl(c);

            if (space)
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    private ISpeechEngine? EngineFor(SpeechVoice voice) => _engines.FirstOrDefault(e => e.Kind == voice.Engine);

    private static string CacheKey(string renderKey) => $"speech-render:{renderKey}";
}
