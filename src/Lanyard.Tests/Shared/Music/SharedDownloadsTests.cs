using Lanyard.Shared.Music;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Music;

[TestClass]
public class SharedDownloadsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task TwoRequestsForTheSameSong_ShareOneDownload()
    {
        SharedDownloads<string> downloads = new();
        Guid song = Guid.NewGuid();
        TaskCompletionSource<string> finish = new();
        int started = 0;

        Task<string> Start()
        {
            Interlocked.Increment(ref started);
            return finish.Task;
        }

        Task<string> first = downloads.GetOrStartAsync(song, Start);
        Task<string> second = downloads.GetOrStartAsync(song, Start);

        finish.SetResult("song.mp3");

        Assert.AreEqual("song.mp3", await first.WaitAsync(Timeout));
        Assert.AreEqual("song.mp3", await second.WaitAsync(Timeout));
        Assert.AreEqual(1, started);
    }

    [TestMethod]
    public async Task DifferentSongs_DownloadSideBySide()
    {
        SharedDownloads<string> downloads = new();
        TaskCompletionSource<string> slow = new();

        Task<string> background = downloads.GetOrStartAsync(Guid.NewGuid(), () => slow.Task);
        string wanted = await downloads.GetOrStartAsync(Guid.NewGuid(), () => Task.FromResult("wanted.mp3")).WaitAsync(Timeout);

        Assert.AreEqual("wanted.mp3", wanted, "A song someone asked for doesn't wait behind another song's download.");
        Assert.IsFalse(background.IsCompleted);

        slow.SetResult("background.mp3");
        await background.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task GivingUpWaiting_LeavesTheDownloadRunningForOthers()
    {
        SharedDownloads<string> downloads = new();
        Guid song = Guid.NewGuid();
        TaskCompletionSource<string> finish = new();
        using CancellationTokenSource cts = new();

        Task<string> impatient = downloads.GetOrStartAsync(song, () => finish.Task, cts.Token);
        Task<string> patient = downloads.GetOrStartAsync(song, () => finish.Task);

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => impatient);

        finish.SetResult("song.mp3");
        Assert.AreEqual("song.mp3", await patient.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task AFinishedDownload_IsForgotten_SoTheSongCanBeFetchedAgain()
    {
        SharedDownloads<string> downloads = new();
        Guid song = Guid.NewGuid();
        int started = 0;

        await downloads.GetOrStartAsync(song, () => { started++; return Task.FromResult("a"); }).WaitAsync(Timeout);
        await downloads.GetOrStartAsync(song, () => { started++; return Task.FromResult("b"); }).WaitAsync(Timeout);

        Assert.AreEqual(2, started);
        Assert.AreEqual(0, downloads.RunningCount);
    }
}
