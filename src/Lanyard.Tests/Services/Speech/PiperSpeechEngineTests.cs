using Lanyard.Application.Services.Speech;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Speech;

// Speaks for real, so it needs the voices (scripts/download-voices.sh). Without them it's skipped
// as inconclusive rather than failed: CI doesn't download 94 MB of models for every run.
[TestClass]
public class PiperSpeechEngineTests
{
    [TestMethod]
    [TestCategory("RequiresVoiceModel")]
    public async Task EveryVoiceInTheCatalogue_Speaks()
    {
        using PiperSpeechEngine engine = new(Options.Create(new SpeechOptions { MaxLoadedVoices = 1 }), NullLogger<PiperSpeechEngine>.Instance);

        if (engine.VoicesPath is null || !SpeechVoiceCatalogue.Voices.All(engine.IsInstalled))
        {
            Assert.Inconclusive("The Piper voices aren't installed; run scripts/download-voices.sh.");
        }

        foreach (SpeechVoice voice in SpeechVoiceCatalogue.Voices)
        {
            Result<SpeechSamples> spoken = await engine.SynthesiseAsync("Hello.", voice, 1.0, CancellationToken.None);

            Assert.IsTrue(spoken.IsSuccess, $"{voice.Id}: {spoken.Error}");
            Assert.IsTrue(spoken.Data!.Samples.Length > spoken.Data.SampleRate / 4, $"{voice.Id} said almost nothing");
        }
    }
}
