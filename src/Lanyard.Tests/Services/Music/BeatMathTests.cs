using Lanyard.Application.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Music;

[TestClass]
public class BeatMathTests
{
    private const double Tolerance = 1e-9;

    [TestMethod]
    public void SecondsUntilNextStep_FiredOnGrid_ReturnsExactStepLength()
    {
        // 120 BPM, 1 beat => step length 0.5s, boundaries at 0, 0.5, 1.0, ...
        // Firing exactly on a boundary needs no correction.
        double? result = BeatMath.SecondsUntilNextStep(1.0, 120, 0, 1);

        Assert.IsNotNull(result);
        Assert.AreEqual(0.5, result.Value, Tolerance);
    }

    [TestMethod]
    public void SecondsUntilNextStep_SixtyBpm_OneStepPerSecond()
    {
        // The headline requirement: 60 BPM = 1 step per second.
        double? result = BeatMath.SecondsUntilNextStep(5.0, 60, 0, 1);

        Assert.IsNotNull(result);
        Assert.AreEqual(1.0, result.Value, Tolerance);
    }

    [TestMethod]
    public void SecondsUntilNextStep_FiredSlightlyLate_ShortensDelayToCatchUp()
    {
        // Fired 0.05s after the boundary (within the ±25% correction cap of 0.125s)
        // => the whole error is corrected in one step: 0.5 - 0.05 = 0.45.
        double? result = BeatMath.SecondsUntilNextStep(0.05, 120, 0, 1);

        Assert.IsNotNull(result);
        Assert.AreEqual(0.45, result.Value, Tolerance);
    }

    [TestMethod]
    public void SecondsUntilNextStep_FiredSlightlyEarly_LengthensDelay()
    {
        // Fired 0.02s before the next boundary => stretch the step so the next
        // advance lands on the grid: 0.5 + 0.02 = 0.52.
        double? result = BeatMath.SecondsUntilNextStep(0.48, 120, 0, 1);

        Assert.IsNotNull(result);
        Assert.AreEqual(0.52, result.Value, Tolerance);
    }

    [TestMethod]
    public void SecondsUntilNextStep_FiredVeryLate_CorrectionIsCapped()
    {
        // Fired 0.2s late (beyond the 0.125s cap): the delay shortens only to
        // 0.375s — never further, so a position-estimate jump backwards across a
        // boundary cannot double-fire a step.
        double? result = BeatMath.SecondsUntilNextStep(0.2, 120, 0, 1);

        Assert.IsNotNull(result);
        Assert.AreEqual(0.375, result.Value, Tolerance);
    }

    [TestMethod]
    public void SecondsUntilNextStep_FiredVeryEarly_CorrectionIsCapped()
    {
        // 0.2s before the next boundary (beyond the cap): lengthen only to 0.625s.
        double? result = BeatMath.SecondsUntilNextStep(0.3, 120, 0, 1);

        Assert.IsNotNull(result);
        Assert.AreEqual(0.625, result.Value, Tolerance);
    }

    [TestMethod]
    public void SecondsUntilNextStep_HonoursFirstBeatOffset()
    {
        // 100 BPM => beat 0.6s, boundaries at 0.3, 0.9, 1.5, ... Position 0.9 sits
        // exactly on the grid => a full uncorrected step.
        double? result = BeatMath.SecondsUntilNextStep(0.9, 100, 0.3, 1);

        Assert.IsNotNull(result);
        Assert.AreEqual(0.6, result.Value, 1e-6);
    }

    [TestMethod]
    public void SecondsUntilNextStep_MultiBeatStep_UsesStepLengthGrid()
    {
        // 120 BPM, 4 beats => step length 2.0s; on-grid fire holds a full 2.0s.
        double? result = BeatMath.SecondsUntilNextStep(4.0, 120, 0, 4);

        Assert.IsNotNull(result);
        Assert.AreEqual(2.0, result.Value, Tolerance);
    }

    [TestMethod]
    public void SecondsUntilNextStep_FractionalBeats_SubdividesBeat()
    {
        // 120 BPM, 0.5 beats => step length 0.25s.
        double? result = BeatMath.SecondsUntilNextStep(0.25, 120, 0, 0.5);

        Assert.IsNotNull(result);
        Assert.AreEqual(0.25, result.Value, Tolerance);
    }

    [TestMethod]
    public void SecondsUntilNextStep_PositionBeforeFirstBeat_StaysBounded()
    {
        // Position before the first beat (relative position negative) must still
        // produce a sane, bounded delay.
        double? result = BeatMath.SecondsUntilNextStep(0.5, 60, 2.0, 1);

        Assert.IsNotNull(result);
        Assert.IsGreaterThanOrEqualTo(0.75, result.Value);
        Assert.IsLessThanOrEqualTo(1.25, result.Value);
    }

    [TestMethod]
    public void SecondsUntilNextStep_DelayAlwaysWithinCorrectionBounds()
    {
        // The whole point of the bounded correction: no position jitter can make a
        // step fire early enough to look like a double-fire, or hold long enough to
        // visibly skip. Sweep positions across several grids and check the bounds.
        foreach (double beats in new[] { 0.25, 0.5, 1.0, 3.0, 64.0 })
        {
            double stepLength = beats * (60.0 / 137);

            for (double pos = 0; pos < 5; pos += 0.037)
            {
                double? result = BeatMath.SecondsUntilNextStep(pos, 137, 0.21, beats);

                Assert.IsNotNull(result);
                Assert.IsGreaterThanOrEqualTo(stepLength * 0.75 - 1e-9, result.Value,
                    $"Delay {result.Value} below 75% of step at pos={pos}, beats={beats}");
                Assert.IsLessThanOrEqualTo(stepLength * 1.25 + 1e-9, result.Value,
                    $"Delay {result.Value} above 125% of step at pos={pos}, beats={beats}");
            }
        }
    }

    [TestMethod]
    public void SecondsUntilNextStep_ConvergesOntoGridWithinFewSteps()
    {
        // Simulate a scene started mid-beat with a clean clock: successive advances
        // must land (arbitrarily close to) the grid after a handful of steps.
        double bpm = 115;
        double stepLength = 60.0 / bpm;
        double position = 0.31; // arbitrary off-grid start

        for (int step = 0; step < 6; step++)
        {
            double? delay = BeatMath.SecondsUntilNextStep(position, bpm, 0, 1);
            Assert.IsNotNull(delay);
            position += delay.Value;
        }

        double intoStep = position % stepLength;
        double distanceToGrid = Math.Min(intoStep, stepLength - intoStep);

        Assert.IsLessThanOrEqualTo(0.001, distanceToGrid,
            $"Still {distanceToGrid * 1000:F1}ms off the grid after 6 steps");
    }

    [TestMethod]
    public void SecondsUntilNextStep_InvalidBpm_ReturnsNull()
    {
        Assert.IsNull(BeatMath.SecondsUntilNextStep(1.0, 0, 0, 1));
        Assert.IsNull(BeatMath.SecondsUntilNextStep(1.0, -120, 0, 1));
    }

    [TestMethod]
    public void SecondsUntilNextStep_InvalidBeats_ReturnsNull()
    {
        Assert.IsNull(BeatMath.SecondsUntilNextStep(1.0, 120, 0, 0));
        Assert.IsNull(BeatMath.SecondsUntilNextStep(1.0, 120, 0, -1));
    }
}
