using Lanyard.Application.Services.AudioClips;
using Lanyard.Application.SignalR;
using Lanyard.Shared.SignalR;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.SignalR;

// Hub methods are called by their C# name, and a name that differs from the kiosk's silently
// drops every call, so pin the names the kiosk uses.
[TestClass]
public class SignalRControlHubAudioClipTests
{
    [TestMethod]
    public void TheKiosksReportMethodName_MatchesTheHub()
    {
        Assert.AreEqual(ClientHubMethods.AudioClipPlaybackChanged, nameof(SignalRControlHub.AudioClipPlaybackChanged));
    }

    [TestMethod]
    public void TheCommandNames_AreDistinct()
    {
        string[] names = [ClientHubMethods.PlayAudioClip, ClientHubMethods.StopAudioClip, ClientHubMethods.AudioClipPlaybackChanged];

        Assert.AreEqual(names.Length, names.Distinct().Count());
    }
}
