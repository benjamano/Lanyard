using Lanyard.Application.Services.Projection;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Projection;

[TestClass]
public class GameWaitSettingsTests
{
    private static readonly Guid Kiosk = Guid.NewGuid();

    private static Dictionary<string, string?> Values(string? gameEvent, string? kiosk, string? secondsLeft = null, string? giveUpAfter = null) => new()
    {
        [GameWaitStepNames.GameEventParameter] = gameEvent,
        [GameWaitStepNames.LaserKioskParameter] = kiosk,
        [GameWaitStepNames.SecondsLeftParameter] = secondsLeft,
        [GameWaitStepNames.GiveUpAfterParameter] = giveUpAfter
    };

    [TestMethod]
    public void Parse_ReadsAValidStep()
    {
        Result<GameWaitSettings> result = GameWaitSettings.Parse(Values("GameEnds", Kiosk.ToString(), giveUpAfter: "20"));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(new GameWaitSettings(ProjectionGameEvent.GameEnds, Kiosk, null, 20), result.Data);
        Assert.AreEqual(TimeSpan.FromMinutes(20), result.Data!.Timeout);
    }

    [TestMethod]
    public void Parse_BlankOrZeroGiveUpAfter_WaitsForever()
    {
        Assert.IsNull(GameWaitSettings.Parse(Values("GameStarts", Kiosk.ToString())).Data!.Timeout);
        Assert.IsNull(GameWaitSettings.Parse(Values("GameStarts", Kiosk.ToString(), giveUpAfter: "0")).Data!.Timeout);
    }

    [TestMethod]
    public void Parse_IgnoresSecondsLeft_UnlessWaitingForTimeLeft()
    {
        Result<GameWaitSettings> result = GameWaitSettings.Parse(Values("GetReady", Kiosk.ToString(), secondsLeft: "not a number"));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNull(result.Data!.SecondsLeft);
    }

    [TestMethod]
    public void Parse_TimeLeftBelow_NeedsSecondsLeft()
    {
        Assert.AreEqual(45, GameWaitSettings.Parse(Values("TimeLeftBelow", Kiosk.ToString(), secondsLeft: "45")).Data!.SecondsLeft);

        foreach (string? bad in new[] { null, "", "0", "-5", "3601", "1.5", "lots" })
        {
            Result<GameWaitSettings> result = GameWaitSettings.Parse(Values("TimeLeftBelow", Kiosk.ToString(), secondsLeft: bad));
            Assert.IsFalse(result.IsSuccess, $"Seconds Left \"{bad}\" should be refused.");
            StringAssert.Contains(result.Error, "Seconds Left");
        }
    }

    [TestMethod]
    public void Parse_RefusesAMissingOrUnknownEvent()
    {
        foreach (string? bad in new[] { null, " ", "GameOver", "2", "GetReady, GameEnds", "getready" })
        {
            Assert.IsFalse(GameWaitSettings.Parse(Values(bad, Kiosk.ToString())).IsSuccess, $"Event \"{bad}\" should be refused.");
        }
    }

    [TestMethod]
    public void Parse_RefusesAMissingOrInvalidKiosk()
    {
        foreach (string? bad in new[] { null, "", "not-a-guid", Guid.Empty.ToString() })
        {
            Assert.IsFalse(GameWaitSettings.Parse(Values("GameEnds", bad)).IsSuccess, $"Kiosk \"{bad}\" should be refused.");
        }
    }

    [TestMethod]
    public void Parse_RefusesAnOutOfRangeGiveUpAfter()
    {
        foreach (string bad in new[] { "-1", "241", "ten" })
        {
            Result<GameWaitSettings> result = GameWaitSettings.Parse(Values("GameEnds", Kiosk.ToString(), giveUpAfter: bad));
            Assert.IsFalse(result.IsSuccess, $"Give Up After \"{bad}\" should be refused.");
            StringAssert.Contains(result.Error, "Give Up After");
        }
    }
}
