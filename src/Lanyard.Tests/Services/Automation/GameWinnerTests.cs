#nullable enable

using Lanyard.Application.Services;
using Lanyard.Shared.Enum;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Automation;

[TestClass]
public class GameWinnerTests
{
    [TestMethod]
    public void Determine_PicksTheTeamWithTheHighestTotal()
    {
        Assert.AreEqual(Team.Red, GameWinner.Determine([new(1, Team.Red, 600), new(2, Team.Red, 500), new(3, Team.Green, 1000)]), "Red's 1100 beats Green's 1000.");
        Assert.AreEqual(Team.Green, GameWinner.Determine([new(1, Team.Red, 100), new(2, Team.Green, 900)]));
    }

    [TestMethod]
    public void Determine_HasNoWinnerOnADraw()
    {
        Assert.IsNull(GameWinner.Determine([new(1, Team.Red, 700), new(2, Team.Green, 700)]));
    }

    [TestMethod]
    public void Determine_HasNoWinnerInASoloGame()
    {
        Assert.IsNull(GameWinner.Determine([new(1, null, 5000), new(2, null, 300)]));
    }

    [TestMethod]
    public void Determine_HasNoWinnerWhenOnlyOneTeamPlayed()
    {
        Assert.IsNull(GameWinner.Determine([new(1, Team.Red, 5000), new(2, Team.Red, 300)]), "Beating nobody isn't winning.");
    }

    [TestMethod]
    public void Determine_HasNoWinnerInAnEmptyGame()
    {
        Assert.IsNull(GameWinner.Determine([]));
    }

    [TestMethod]
    public void Determine_IgnoresPlayersWithNoTeam()
    {
        Assert.AreEqual(Team.Green, GameWinner.Determine([new(1, Team.Red, 100), new(2, Team.Green, 200), new(3, null, 9000)]));
    }
}
