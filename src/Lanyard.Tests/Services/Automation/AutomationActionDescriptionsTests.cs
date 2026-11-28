#nullable enable

using Lanyard.Application.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Automation;

/// <summary>The plain-words lines the Run now (test) confirm and result dialogs show.</summary>
[TestClass]
public class AutomationActionDescriptionsTests
{
    private static readonly Guid Kiosk = Guid.NewGuid();
    private static readonly Guid Thing = Guid.NewGuid();

    private static string Describe(string type, string json, bool namesKnown = true) =>
        AutomationActionDescriptions.Describe(
            type,
            json,
            id => namesKnown && id == Kiosk ? "Arena 1" : null,
            (kind, id) => namesKnown && id == Thing ? "Party" : null);

    [TestMethod]
    [DataRow(AutomationActionTypes.MusicControl, "\"Operation\":\"Play\"", "Play the music on Arena 1")]
    [DataRow(AutomationActionTypes.MusicControl, "\"Operation\":\"Play\",\"PlaylistId\":\"THING\"", "Play playlist 'Party' on Arena 1")]
    [DataRow(AutomationActionTypes.MusicControl, "\"Operation\":\"Pause\"", "Pause the music on Arena 1")]
    [DataRow(AutomationActionTypes.MusicControl, "\"Operation\":\"Play\",\"PlaylistId\":\"THING\",\"ResumeIfLoaded\":true,\"Shuffle\":\"On\",\"Repeat\":\"All\"", "Play playlist 'Party' on Arena 1, carrying on if it's already loaded, shuffle on, repeat the playlist")]
    [DataRow(AutomationActionTypes.MusicControl, "\"Operation\":\"Play\",\"ResumeIfLoaded\":true,\"Shuffle\":\"Off\",\"Repeat\":\"One\"", "Play the music on Arena 1, shuffle off, repeat the song")]
    [DataRow(AutomationActionTypes.MusicControl, "\"Operation\":\"Pause\",\"Shuffle\":\"On\"", "Pause the music on Arena 1")]
    [DataRow(AutomationActionTypes.StartProjectionProgram, "\"ProjectionProgramId\":\"THING\"", "Start projection program 'Party' on Arena 1")]
    [DataRow(AutomationActionTypes.StopProjectionProgram, "", "Stop the projection program on Arena 1")]
    [DataRow(AutomationActionTypes.ProjectionProgramControl, "\"Operation\":\"Start\",\"ProjectionProgramId\":\"THING\"", "Start projection program 'Party' on Arena 1")]
    [DataRow(AutomationActionTypes.ProjectionProgramControl, "\"Operation\":\"SkipToNext\"", "Skip to the next projection step on Arena 1")]
    [DataRow(AutomationActionTypes.DmxSceneControl, "\"Operation\":\"StartScene\",\"SceneId\":\"THING\",\"HoldMilliseconds\":2500", "Start lighting scene 'Party' for 2.5 seconds on Arena 1")]
    [DataRow(AutomationActionTypes.DmxSceneControl, "\"Operation\":\"StopAllScenes\"", "Stop all lighting scenes on Arena 1")]
    [DataRow(AutomationActionTypes.PlayAudioClip, "\"AudioClipId\":\"THING\"", "Play audio clip 'Party' on Arena 1")]
    public void Describe_SaysWhatTheActionDoesAndWhere(string type, string extra, string expected)
    {
        string json = "{\"TargetClientId\":\"" + Kiosk + "\"" + (extra.Length > 0 ? "," + extra.Replace("THING", Thing.ToString()) : "") + "}";

        Assert.AreEqual(expected, Describe(type, json));
    }

    [TestMethod]
    public void Describe_SaysWhenAKioskOrSceneHasGone()
    {
        string json = $"{{\"TargetClientId\":\"{Kiosk}\",\"Operation\":\"StartScene\",\"SceneId\":\"{Thing}\"}}";

        Assert.AreEqual("Start lighting scene 'a deleted scene' on a deleted kiosk", Describe(AutomationActionTypes.DmxSceneControl, json, namesKnown: false));
    }

    [TestMethod]
    public void Describe_SaysWhenNoKioskHasBeenChosen_OrTheSettingsCantBeRead()
    {
        Assert.AreEqual("Pause the music (no kiosk chosen)", Describe(AutomationActionTypes.MusicControl, "{\"Operation\":\"Pause\"}"));
        Assert.AreEqual("An action whose settings couldn't be read", Describe(AutomationActionTypes.MusicControl, "not json"));
    }
}
