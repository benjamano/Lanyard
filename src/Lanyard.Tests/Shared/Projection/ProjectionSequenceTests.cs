using Lanyard.Shared.Projection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.Projection;

[TestClass]
public class ProjectionSequenceTests
{
    [TestMethod]
    public void EffectiveHold_ZeroFallsBackToTheDefault()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(ProjectionSequence.DefaultHoldMilliseconds), ProjectionSequence.EffectiveHold(0));
        Assert.AreEqual(TimeSpan.FromSeconds(12), ProjectionSequence.EffectiveHold(12000));
    }

    [TestMethod]
    public void Advance_MovesToTheNextStepWithinAPass()
    {
        ProjectionStepPosition next = ProjectionSequence.Advance(0, stepCount: 3, completedPasses: 0, repeatInfinitely: false, repeatCount: 0);

        Assert.AreEqual(new ProjectionStepPosition(1, 0, false), next);
    }

    [TestMethod]
    public void Advance_RepeatCountZero_PlaysTheProgramOnce()
    {
        ProjectionStepPosition next = ProjectionSequence.Advance(2, stepCount: 3, completedPasses: 0, repeatInfinitely: false, repeatCount: 0);

        Assert.IsTrue(next.Finished);
    }

    [TestMethod]
    public void Advance_RepeatCountCountsExtraPasses()
    {
        // repeatCount 2 = three passes in total.
        int index = 0;
        int passes = 0;
        int stepsPlayed = 0;

        while (true)
        {
            stepsPlayed++;
            ProjectionStepPosition next = ProjectionSequence.Advance(index, stepCount: 2, passes, repeatInfinitely: false, repeatCount: 2);

            if (next.Finished)
            {
                break;
            }

            index = next.Index;
            passes = next.CompletedPasses;
        }

        Assert.AreEqual(6, stepsPlayed);
    }

    [TestMethod]
    public void Advance_RepeatInfinitely_WrapsForever()
    {
        ProjectionStepPosition next = ProjectionSequence.Advance(1, stepCount: 2, completedPasses: 500, repeatInfinitely: true, repeatCount: 0);

        Assert.AreEqual(new ProjectionStepPosition(0, 501, false), next);
    }

    [TestMethod]
    public void Advance_SingleStepProgram_WrapsToItself()
    {
        ProjectionStepPosition next = ProjectionSequence.Advance(0, stepCount: 1, completedPasses: 0, repeatInfinitely: true, repeatCount: 0);

        Assert.AreEqual(0, next.Index);
        Assert.IsFalse(next.Finished);
    }

    [TestMethod]
    public void ComputeHoldDelayMilliseconds_TrimsTheLastTickAndNeverSpinsWhilePaused()
    {
        TimeSpan hold = TimeSpan.FromSeconds(1);

        Assert.AreEqual(ProjectionSequence.TickMilliseconds, ProjectionSequence.ComputeHoldDelayMilliseconds(hold, TimeSpan.Zero, isPaused: false));
        Assert.AreEqual(50, ProjectionSequence.ComputeHoldDelayMilliseconds(hold, TimeSpan.FromMilliseconds(950), isPaused: false));
        Assert.AreEqual(1, ProjectionSequence.ComputeHoldDelayMilliseconds(hold, TimeSpan.FromSeconds(2), isPaused: false));
        Assert.AreEqual(ProjectionSequence.TickMilliseconds, ProjectionSequence.ComputeHoldDelayMilliseconds(hold, TimeSpan.FromMilliseconds(999), isPaused: true));
    }
}
