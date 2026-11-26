using Lanyard.Application.Services.Speech;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Speech;

[TestClass]
public class SpeechRenderServiceTests
{
    private static (SpeechRenderService Service, FakeSpeechEngine Engine) Create()
    {
        FakeSpeechEngine engine = new();
        return (new SpeechRenderService([engine], NullLogger<SpeechRenderService>.Instance), engine);
    }

    [TestMethod]
    public async Task Render_MakesAWavFile_PaddedAtBothEnds()
    {
        (SpeechRenderService service, FakeSpeechEngine engine) = Create();

        Result<RenderedSpeech> result = await service.RenderAsync("Hello there", new SpeechVoiceSettings("piper-cori"));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(result.Data!.Wav, 0, 4));
        double spoken = "Hello there".Length * 0.06;
        Assert.AreEqual(spoken + SpeechRules.LeadSeconds + SpeechRules.TailSeconds, result.Data.DurationSeconds, 0.1);
        Assert.AreEqual(1.0, engine.Calls.Single().Speed, 1e-9);
    }

    [TestMethod]
    public async Task Render_AsksTheEngineToSpeakSlowerToRaiseThePitch_KeepingTheLength()
    {
        (SpeechRenderService service, FakeSpeechEngine engine) = Create();

        Result<RenderedSpeech> normal = await service.RenderAsync("Hello there", new SpeechVoiceSettings("piper-cori"));
        Result<RenderedSpeech> higher = await service.RenderAsync("Hello there", new SpeechVoiceSettings("piper-cori", Pitch: 12));

        Assert.AreEqual(0.5, engine.Calls[1].Speed, 1e-9);
        Assert.AreEqual(normal.Data!.DurationSeconds, higher.Data!.DurationSeconds, 0.05);
    }

    [TestMethod]
    public async Task Render_OfTheSameWordsAndVoiceTwice_SpeaksOnlyOnce()
    {
        (SpeechRenderService service, FakeSpeechEngine engine) = Create();

        await service.RenderAsync("Hello there", new SpeechVoiceSettings("piper-cori"));
        await service.RenderAsync("Hello   there", new SpeechVoiceSettings("piper-cori"));
        await service.RenderAsync("Hello there", new SpeechVoiceSettings("piper-alba"));

        Assert.AreEqual(2, engine.Calls.Count);
    }

    [TestMethod]
    public void RenderKey_ChangesWithAnythingThatChangesTheSound_ButNotWithSpacing()
    {
        (SpeechRenderService service, _) = Create();
        string key = service.RenderKeyFor("Hello there", new SpeechVoiceSettings("piper-cori"));

        Assert.AreEqual(key, service.RenderKeyFor(" Hello\n there ", new SpeechVoiceSettings("piper-cori")));
        Assert.AreNotEqual(key, service.RenderKeyFor("Hello there!", new SpeechVoiceSettings("piper-cori")));
        Assert.AreNotEqual(key, service.RenderKeyFor("Hello there", new SpeechVoiceSettings("piper-alba")));
        Assert.AreNotEqual(key, service.RenderKeyFor("Hello there", new SpeechVoiceSettings("piper-cori", 1.1)));
        Assert.AreNotEqual(key, service.RenderKeyFor("Hello there", new SpeechVoiceSettings("piper-cori", Pitch: 1)));
        Assert.AreEqual(64, key.Length);
    }

    [TestMethod]
    public async Task Render_FailsClearly_WhenTheVoiceIsNotInstalled()
    {
        (SpeechRenderService service, FakeSpeechEngine engine) = Create();
        engine.Installed = false;

        Result<RenderedSpeech> result = await service.RenderAsync("Hello", new SpeechVoiceSettings("piper-cori"));

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "isn't installed");
        Assert.IsFalse(service.ListVoices().Any(v => v.IsInstalled));
    }

    [TestMethod]
    public void Validate_RefusesTextWithNoWords()
    {
        (SpeechRenderService service, _) = Create();

        Assert.IsNotNull(service.Validate("?!...", new SpeechVoiceSettings("piper-cori")));
        Assert.IsNull(service.Validate("Two minutes!", new SpeechVoiceSettings("piper-cori")));
    }

    [TestMethod]
    public void Clean_TurnsControlCharactersIntoSpaces_AndCollapsesRuns()
    {
        Assert.AreEqual("Red team, go", SpeechRenderService.Clean("  Red\tteam,\u0007\r\n  go  "));
        Assert.AreEqual(string.Empty, SpeechRenderService.Clean(null));
    }

    [TestMethod]
    public void Catalogue_VoiceIdsAreUnique_AndTheDefaultExists()
    {
        Assert.AreEqual(SpeechVoiceCatalogue.Voices.Count, SpeechVoiceCatalogue.Voices.Select(v => v.Id).Distinct().Count());
        Assert.IsNotNull(SpeechVoiceCatalogue.Find(SpeechVoiceCatalogue.DefaultVoiceId));
    }
}
