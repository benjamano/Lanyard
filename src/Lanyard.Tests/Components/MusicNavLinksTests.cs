using Lanyard.App.Components.Layout.Staff;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Components;

// The side menu's Music group: each link follows its page's access rule, and the group hides
// when none of them apply.
[TestClass]
public class MusicNavLinksTests
{
    [TestMethod]
    public void Manager_SeesAllThree_WhenAudioClipsEnabled()
    {
        MusicNavLinks links = MusicNavLinks.For(canControlMusic: true, canPlayAudioClips: true, audioClipsEnabled: true, isManager: true);

        Assert.AreEqual(new MusicNavLinks(true, true, true), links);
        Assert.IsTrue(links.Any);
    }

    [TestMethod]
    public void AudioClips_Hidden_WhenCompanyFeatureOff()
    {
        MusicNavLinks links = MusicNavLinks.For(canControlMusic: true, canPlayAudioClips: true, audioClipsEnabled: false, isManager: true);

        Assert.IsFalse(links.AudioClips);
        Assert.IsTrue(links.Player);
        Assert.IsTrue(links.Schedule);
    }

    [TestMethod]
    public void MusicControllerOnly_SeesPlayer_NotScheduleOrClips()
    {
        MusicNavLinks links = MusicNavLinks.For(canControlMusic: true, canPlayAudioClips: false, audioClipsEnabled: true, isManager: false);

        Assert.AreEqual(new MusicNavLinks(Player: true, AudioClips: false, Schedule: false), links);
    }

    [TestMethod]
    public void AudioClipsRoleOnly_SeesJustAudioClips()
    {
        MusicNavLinks links = MusicNavLinks.For(canControlMusic: false, canPlayAudioClips: true, audioClipsEnabled: true, isManager: false);

        Assert.AreEqual(new MusicNavLinks(Player: false, AudioClips: true, Schedule: false), links);
        Assert.IsTrue(links.Any);
    }

    [TestMethod]
    public void NoMusicAccess_HidesGroup()
    {
        MusicNavLinks links = MusicNavLinks.For(canControlMusic: false, canPlayAudioClips: true, audioClipsEnabled: false, isManager: false);

        Assert.IsFalse(links.Any);
    }
}
