using Lanyard.Shared.Audio;
using Lanyard.Shared.SignalR;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Audio;

[TestClass]
public class MusicEqualiserTests
{
    // Measures a filter chain's gain at one frequency: run a sine through it until it settles,
    // then compare the output's peak with the input's.
    private static double GainDbAt(double frequency, int sampleRate, Func<float, float> process)
    {
        int settle = sampleRate;
        int measure = sampleRate / 2;
        double peak = 0;

        for (int i = 0; i < settle + measure; i++)
        {
            float input = (float)(0.25 * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
            float output = process(input);

            if (i >= settle)
            {
                peak = Math.Max(peak, Math.Abs(output));
            }
        }

        return 20 * Math.Log10(peak / 0.25);
    }

    [TestMethod]
    public void BassShelf_LiftsTheLowsAndLeavesTheHighs()
    {
        BiQuadFilter filter = BiQuadFilter.LowShelf(48000, MusicEqualiserSettings.BassHz, 6);
        Assert.AreEqual(6, GainDbAt(30, 48000, filter.Process), 0.5);

        filter = BiQuadFilter.LowShelf(48000, MusicEqualiserSettings.BassHz, 6);
        Assert.AreEqual(0, GainDbAt(5000, 48000, filter.Process), 0.3);
    }

    [TestMethod]
    public void TrebleShelf_LiftsTheHighsAndLeavesTheLows()
    {
        BiQuadFilter filter = BiQuadFilter.HighShelf(44100, MusicEqualiserSettings.TrebleHz, 6);
        Assert.AreEqual(6, GainDbAt(16000, 44100, filter.Process), 0.7);

        filter = BiQuadFilter.HighShelf(44100, MusicEqualiserSettings.TrebleHz, 6);
        Assert.AreEqual(0, GainDbAt(200, 44100, filter.Process), 0.3);
    }

    [TestMethod]
    public void MidBell_PeaksAtItsCentre()
    {
        BiQuadFilter filter = BiQuadFilter.Peaking(48000, MusicEqualiserSettings.MidHz, MusicEqualiserSettings.MidQ, 6);
        Assert.AreEqual(6, GainDbAt(1000, 48000, filter.Process), 0.2);

        filter = BiQuadFilter.Peaking(48000, MusicEqualiserSettings.MidHz, MusicEqualiserSettings.MidQ, 6);
        Assert.AreEqual(0, GainDbAt(40, 48000, filter.Process), 0.5);
    }

    [TestMethod]
    [DataRow(44100, 12)]
    [DataRow(44100, -12)]
    [DataRow(48000, 12)]
    [DataRow(48000, -12)]
    [DataRow(22050, 12)]
    public void FullBoostsAndCuts_StayStable(int sampleRate, int db)
    {
        MusicEqualiserProcessor processor = new(sampleRate, 2, new MusicEqualiserSettings(db, db, db));
        float[] buffer = new float[sampleRate * 2];
        Random random = new(1);

        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (float)(random.NextDouble() * 2 - 1) * 0.5f;
        }

        processor.Process(buffer, 0, buffer.Length);

        Assert.IsTrue(buffer.All(float.IsFinite), "Expected no NaN or infinity");
        Assert.IsTrue(buffer.Max(Math.Abs) < 8f, "Expected the filters not to run away");
    }

    [TestMethod]
    public void FlatProcessor_LeavesSamplesUntouched()
    {
        MusicEqualiserProcessor processor = new(48000, 2, MusicEqualiserSettings.Flat);
        float[] buffer = [0.1f, -0.2f, 0.3f, -0.4f];

        processor.Process(buffer, 0, buffer.Length);

        CollectionAssert.AreEqual(new[] { 0.1f, -0.2f, 0.3f, -0.4f }, buffer);
    }

    [TestMethod]
    public void Processor_FiltersEachChannelOnItsOwn()
    {
        // Bass up on a stereo stream with a low tone on the left only: the right must stay silent.
        MusicEqualiserProcessor processor = new(48000, 2, new MusicEqualiserSettings(12, 0, 0));
        float[] buffer = new float[48000 * 2];

        for (int frame = 0; frame < 48000; frame++)
        {
            buffer[frame * 2] = (float)(0.1 * Math.Sin(2 * Math.PI * 50 * frame / 48000));
        }

        processor.Process(buffer, 0, buffer.Length);

        for (int frame = 0; frame < 48000; frame++)
        {
            Assert.AreEqual(0f, buffer[frame * 2 + 1]);
        }
    }

    [TestMethod]
    public void Headroom_IsHalfTheBiggestBoost()
    {
        Assert.AreEqual(1f, MusicEqualiserProcessor.HeadroomFor(MusicEqualiserSettings.Flat), 0.0001f);
        Assert.AreEqual(1f, MusicEqualiserProcessor.HeadroomFor(new(-6, -3, -12)), 0.0001f);
        Assert.AreEqual(Math.Pow(10, -3.0 / 20), MusicEqualiserProcessor.HeadroomFor(new(6, 2, -4)), 0.0001);
    }

    [TestMethod]
    public void Update_ChangesTheSoundWithoutAClick()
    {
        MusicEqualiserProcessor processor = new(48000, 1, MusicEqualiserSettings.Flat);
        float[] first = Enumerable.Range(0, 4800).Select(i => (float)(0.2 * Math.Sin(2 * Math.PI * 80 * i / 48000))).ToArray();
        processor.Process(first, 0, first.Length);

        processor.Update(new MusicEqualiserSettings(6, 0, 0));

        float[] second = Enumerable.Range(4800, 4800).Select(i => (float)(0.2 * Math.Sin(2 * Math.PI * 80 * i / 48000))).ToArray();
        processor.Process(second, 0, second.Length);

        Assert.AreEqual(new MusicEqualiserSettings(6, 0, 0), processor.Settings);
        // A filter reset mid-tone would jump; a gradual change keeps each step small.
        Assert.IsTrue(Math.Abs(second[0] - first[^1]) < 0.05f);
    }

    [TestMethod]
    public void Clamp_KeepsEachBandWithinTwelveDecibels()
    {
        Assert.AreEqual(new MusicEqualiserSettings(12, -12, 3), new MusicEqualiserSettings(20, -99, 3).Clamp());
    }

    [TestMethod]
    public void IsFlat_OnlyWhenEveryBandIsZero()
    {
        Assert.IsTrue(MusicEqualiserSettings.Flat.IsFlat);
        Assert.IsFalse(new MusicEqualiserSettings(0, 1, 0).IsFlat);
    }

    [TestMethod]
    public void Presets_AreInRangeUniqueAndStartWithFlat()
    {
        Assert.AreEqual("Flat", MusicEqualiserSettings.Presets[0].Name);
        Assert.IsTrue(MusicEqualiserSettings.Presets[0].Settings.IsFlat);

        foreach (MusicEqualiserPreset preset in MusicEqualiserSettings.Presets)
        {
            Assert.AreEqual(preset.Settings, preset.Settings.Clamp(), preset.Name);
        }

        Assert.AreEqual(MusicEqualiserSettings.Presets.Count, MusicEqualiserSettings.Presets.Select(p => p.Settings).Distinct().Count());
        Assert.AreEqual("Bass Boost", new MusicEqualiserSettings(6, 0, 1).MatchingPreset?.Name);
        Assert.IsNull(new MusicEqualiserSettings(5, 5, 5).MatchingPreset);
    }

    [TestMethod]
    [DataRow("1.0.48", true)]
    [DataRow("1.1.0", true)]
    [DataRow("1.0.47", false)]
    [DataRow("1.0.48-beta.1", false)]
    [DataRow(null, false)]
    [DataRow("garbage", false)]
    public void IsSupportedBy_NeedsKioskVersion1048(string? version, bool expected)
    {
        Assert.AreEqual(expected, MusicEqualiserSettings.IsSupportedBy(version));
    }

    [TestMethod]
    public void HubMethodName_NeverChanges()
    {
        // Kiosks in the field listen for this exact string.
        Assert.AreEqual("ReceiveEqualiserSettings", ClientHubMethods.ReceiveEqualiserSettings);
    }

    [TestMethod]
    public void Settings_RoundTripThroughJson()
    {
        // SignalR's JSON protocol and the kiosk's music-eq.json both rely on this.
        MusicEqualiserSettings settings = new(6, -2, 3);
        string json = System.Text.Json.JsonSerializer.Serialize(settings);

        Assert.AreEqual(settings, System.Text.Json.JsonSerializer.Deserialize<MusicEqualiserSettings>(json));
        Assert.AreEqual(settings, System.Text.Json.JsonSerializer.Deserialize<MusicEqualiserSettings>(json.ToLowerInvariant(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }
}
