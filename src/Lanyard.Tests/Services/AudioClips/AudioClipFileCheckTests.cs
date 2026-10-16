using Lanyard.Application.Services.AudioClips;
using Lanyard.Infrastructure.DTO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;

namespace Lanyard.Tests.Services.AudioClips;

[TestClass]
public class AudioClipFileCheckTests
{
    private static Result<double> Inspect(string fileName, byte[] bytes) =>
        AudioClipFileCheck.Inspect(fileName, bytes.LongLength, () => new MemoryStream(bytes));

    [TestMethod]
    public void AShortWav_PassesWithItsLength()
    {
        Result<double> result = Inspect("closing.wav", TestWav.OfSeconds(12));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(12, result.Data, 0.1);
    }

    [TestMethod]
    public void AnUnsupportedExtension_IsRefused()
    {
        Result<double> result = Inspect("closing.ogg", TestWav.OfSeconds(2));

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "MP3");
    }

    [TestMethod]
    public void ARenamedTextFile_IsRefused()
    {
        Result<double> result = Inspect("closing.mp3", Encoding.UTF8.GetBytes("this is not audio at all, just some text"));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public void AnEmptyFile_IsRefused()
    {
        Assert.IsFalse(Inspect("closing.wav", []).IsSuccess);
    }

    [TestMethod]
    public void AFileOverTheSizeLimit_IsRefusedWithoutBeingRead()
    {
        bool opened = false;

        Result<double> result = AudioClipFileCheck.Inspect("big.wav", AudioClipFileCheck.MaxBytes + 1, () =>
        {
            opened = true;
            return Stream.Null;
        });

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "25 MB");
        Assert.IsFalse(opened);
    }

    [TestMethod]
    public void AClipOverFiveMinutes_IsRefused()
    {
        Result<double> result = Inspect("long.wav", TestWav.OfSeconds(301));

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "5 minutes");
    }
}
