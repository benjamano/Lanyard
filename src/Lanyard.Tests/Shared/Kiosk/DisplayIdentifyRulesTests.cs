using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.Kiosk;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Kiosk;

[TestClass]
public class DisplayIdentifyRulesTests
{
    [TestMethod]
    public void ScreensAreNumberedFromOne_LikeTheRestOfLanyard()
    {
        Assert.AreEqual(1, DisplayIdentifyRules.ScreenNumber(0));
        Assert.AreEqual("Screen 3", DisplayIdentifyRules.ScreenTitle(2));
    }

    [TestMethod]
    [DataRow(0, DisplayIdentifyRules.DefaultDurationSeconds)]
    [DataRow(-5, DisplayIdentifyRules.DefaultDurationSeconds)]
    [DataRow(1, DisplayIdentifyRules.MinDurationSeconds)]
    [DataRow(8, 8)]
    [DataRow(600, DisplayIdentifyRules.MaxDurationSeconds)]
    public void ClampDuration_KeepsTheOverlayBriefButReadable(int requested, int expected)
    {
        Assert.AreEqual(expected, DisplayIdentifyRules.ClampDuration(requested));
    }

    [TestMethod]
    public void Describe_MarksTheMainScreen()
    {
        Assert.AreEqual(@"\\.\DISPLAY1 · 1920×1080 · Main screen", DisplayIdentifyRules.Describe(@"\\.\DISPLAY1", 1920, 1080, true));
        Assert.AreEqual(@"\\.\DISPLAY2 · 1280×720", DisplayIdentifyRules.Describe(@"\\.\DISPLAY2", 1280, 720, false));
    }

    [TestMethod]
    public void PickerLabel_LeadsWithTheScreenNumber()
    {
        Assert.AreEqual(@"Screen 2 · \\.\DISPLAY2 (1920x1080)", DisplayIdentifyRules.PickerLabel(1, @"\\.\DISPLAY2", 1920, 1080));
    }

    [TestMethod]
    public void ProgramLine_SaysWhenNothingIsAssigned()
    {
        Assert.AreEqual("No program assigned", DisplayIdentifyRules.ProgramLine(null));
        Assert.AreEqual("No program assigned", DisplayIdentifyRules.ProgramLine("  "));
        Assert.AreEqual("Program: Arena Loop", DisplayIdentifyRules.ProgramLine(" Arena Loop "));
    }

    [TestMethod]
    public void ProgramFor_MatchesByIndex()
    {
        List<DisplayIdentifyLabelDTO> labels = [new() { DisplayIndex = 1, ProgramName = "Arena Loop" }];

        Assert.AreEqual("Arena Loop", DisplayIdentifyRules.ProgramFor(labels, 1));
        Assert.IsNull(DisplayIdentifyRules.ProgramFor(labels, 0));
        Assert.IsNull(DisplayIdentifyRules.ProgramFor(null, 0));
    }

    [TestMethod]
    public void Truncate_CapsLongText()
    {
        Assert.AreEqual("abc", DisplayIdentifyRules.Truncate("abcdef", 3));
        Assert.AreEqual("ab", DisplayIdentifyRules.Truncate("ab", 3));
        Assert.IsNull(DisplayIdentifyRules.Truncate(null, 3));
    }

    private static DisplayIdentifyResultDTO Reply(Guid requestId, params int[] indexes)
    {
        return new DisplayIdentifyResultDTO
        {
            RequestId = requestId,
            Displays = indexes.Select(i => new IdentifiedDisplayDTO { Index = i, DeviceName = $"D{i}" }).ToList()
        };
    }

    [TestMethod]
    public void ValidateReply_AcceptsAGoodReply()
    {
        Guid id = Guid.NewGuid();

        Assert.IsNull(DisplayIdentifyRules.ValidateReply(Reply(id, 0, 1), id));
    }

    [TestMethod]
    public void ValidateReply_RejectsBadReplies()
    {
        Guid id = Guid.NewGuid();

        Assert.IsNotNull(DisplayIdentifyRules.ValidateReply(null, id));
        Assert.IsNotNull(DisplayIdentifyRules.ValidateReply(Reply(Guid.NewGuid(), 0), id), "Wrong request");
        Assert.IsNotNull(DisplayIdentifyRules.ValidateReply(Reply(id), id), "No screens");
        Assert.IsNotNull(DisplayIdentifyRules.ValidateReply(Reply(id, 0, 0), id), "Same screen twice");
        Assert.IsNotNull(DisplayIdentifyRules.ValidateReply(Reply(id, -1), id), "Negative index");
        Assert.IsNotNull(DisplayIdentifyRules.ValidateReply(Reply(id, DisplayIdentifyRules.MaxDisplays), id), "Index out of range");
        Assert.IsNotNull(DisplayIdentifyRules.ValidateReply(Reply(id, Enumerable.Range(0, DisplayIdentifyRules.MaxDisplays + 1).ToArray()), id), "Too many");
    }

    [TestMethod]
    public void SelfTest_ListsTheScreensInOrder()
    {
        List<KioskSelfTestCheckDTO> checks = SelfTestRules.EvaluateDisplays(
        [
            new SelfTestRules.DisplayFacts(1, @"\\.\DISPLAY2", 1280, 720, false),
            new SelfTestRules.DisplayFacts(0, @"\\.\DISPLAY1", 1920, 1080, true)
        ]);

        KioskSelfTestCheckDTO check = checks.Single();
        Assert.AreEqual(SelfTestCheckKeys.DisplaysList, check.Key);
        Assert.AreEqual(SelfTestOutcome.Pass, check.Outcome);
        Assert.AreEqual(@"2 screens. Screen 1: \\.\DISPLAY1 · 1920×1080 · Main screen; Screen 2: \\.\DISPLAY2 · 1280×720.", check.Detail);
    }

    [TestMethod]
    public void SelfTest_NoScreens_Fails()
    {
        KioskSelfTestCheckDTO check = SelfTestRules.EvaluateDisplays([]).Single();

        Assert.AreEqual(SelfTestOutcome.Fail, check.Outcome);
        Assert.AreEqual(SelfTestCodes.NoDisplays, check.Code);
    }
}
