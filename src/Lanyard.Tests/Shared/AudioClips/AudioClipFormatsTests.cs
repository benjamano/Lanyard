using Lanyard.Shared.AudioClips;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.AudioClips;

[TestClass]
public class AudioClipFormatsTests
{
    [TestMethod]
    [DataRow("closing.mp3", ".mp3")]
    [DataRow("Birthday.WAV", ".wav")]
    [DataRow("safety.m4a", ".m4a")]
    [DataRow("notes.txt", null)]
    [DataRow("mp3", null)]
    [DataRow("", null)]
    public void ExtensionOf_OnlyAcceptsPlayableFormats(string fileName, string? expected)
    {
        Assert.AreEqual(expected, AudioClipFormats.ExtensionOf(fileName));
    }

    [TestMethod]
    public void ContentTypeFor_ComesFromTheExtension()
    {
        Assert.AreEqual("audio/mpeg", AudioClipFormats.ContentTypeFor("a.mp3"));
        Assert.AreEqual("audio/wav", AudioClipFormats.ContentTypeFor("a.wav"));
        Assert.AreEqual("application/octet-stream", AudioClipFormats.ContentTypeFor("a.exe"));
    }

    [TestMethod]
    public void CacheFileName_IsBuiltOnlyFromSafeParts()
    {
        Guid clipId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        string? name = AudioClipFormats.CacheFileName(clipId, @"..\..\Windows\evil", ".MP3");

        Assert.AreEqual("11111111222233334444555555555555_Windowsevil.mp3", name);
    }

    [TestMethod]
    public void CacheFileName_RefusesAnUnplayableExtension()
    {
        Assert.IsNull(AudioClipFormats.CacheFileName(Guid.NewGuid(), "abc", ".exe"));
        Assert.IsNull(AudioClipFormats.CacheFileName(Guid.NewGuid(), "abc", "/../x.mp3"));
    }

    [TestMethod]
    public void CacheFileName_GivesABlankVersionAPlaceholder()
    {
        Guid clipId = Guid.NewGuid();

        Assert.AreEqual($"{clipId:N}_0.wav", AudioClipFormats.CacheFileName(clipId, null, ".wav"));
    }

    [TestMethod]
    public void ClipIdFromCacheFileName_ReadsBackTheClip()
    {
        Guid clipId = Guid.NewGuid();
        string name = AudioClipFormats.CacheFileName(clipId, "v1", ".mp3")!;

        Assert.AreEqual(clipId, AudioClipFormats.ClipIdFromCacheFileName(name));
        Assert.IsNull(AudioClipFormats.ClipIdFromCacheFileName("cache-metadata.json"));
    }
}
