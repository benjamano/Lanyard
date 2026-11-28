using Lanyard.Infrastructure.Enum;

namespace Lanyard.Application.Services.Speech;

/// <param name="Id">Stored on clips, so never change one; retire a voice by removing it here.</param>
/// <param name="ModelFolder">The voice's folder under the voices path (as published by sherpa-onnx).</param>
/// <param name="SpeakerId">Which speaker, for a voice trained on many people (VCTK); 0 otherwise.</param>
public sealed record SpeechVoice(
    string Id,
    string Name,
    string Description,
    SpeechEngineKind Engine,
    string ModelFolder,
    int SpeakerId,
    string Licence);

/// <summary>
/// The voices Lanyard offers. All British English Piper voices whose training data allows
/// commercial use (CC BY 4.0, or CC BY-SA 4.0 for the northern voice); each voice's MODEL_CARD
/// is in its folder. The Dockerfile downloads exactly these folders.
/// </summary>
/// <remarks>
/// semaine is left out on purpose: its licence is non-commercial. The VCTK speakers were chosen
/// by measuring each one's pitch, so "man" and "woman" match what you hear.
/// </remarks>
public static class SpeechVoiceCatalogue
{
    public const string DefaultVoiceId = "piper-cori";

    private const string Vctk = "vits-piper-en_GB-vctk-medium-int8";
    private const string VctkLicence = "CSTR VCTK Corpus, CC BY 4.0";

    public static readonly IReadOnlyList<SpeechVoice> Voices =
    [
        new("piper-cori", "Cori", "English woman, warm and clear", SpeechEngineKind.Piper, "vits-piper-en_GB-cori-medium-int8", 0, "Hi-Fi TTS dataset (NVIDIA), CC BY 4.0"),
        new("piper-alba", "Alba", "Scottish woman", SpeechEngineKind.Piper, "vits-piper-en_GB-alba-medium-int8", 0, "CC BY 4.0"),
        new("piper-northern-man", "Northern", "Northern English man", SpeechEngineKind.Piper, "vits-piper-en_GB-northern_english_male-medium-int8", 0, "CC BY-SA 4.0"),
        new("piper-vctk-p254", "Grant", "English man, deep", SpeechEngineKind.Piper, Vctk, 76, VctkLicence),
        new("piper-vctk-p237", "Callum", "Scottish man, deep", SpeechEngineKind.Piper, Vctk, 102, VctkLicence),
        new("piper-vctk-p226", "Oliver", "English man", SpeechEngineKind.Piper, Vctk, 95, VctkLicence),
        new("piper-vctk-p243", "Jack", "London man", SpeechEngineKind.Piper, Vctk, 81, VctkLicence),
        new("piper-vctk-p225", "Emily", "Southern English woman", SpeechEngineKind.Piper, Vctk, 107, VctkLicence),
        new("piper-vctk-p228", "Sophie", "English woman, bright", SpeechEngineKind.Piper, Vctk, 90, VctkLicence)
    ];

    public static SpeechVoice? Find(string? id) =>
        Voices.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
}
