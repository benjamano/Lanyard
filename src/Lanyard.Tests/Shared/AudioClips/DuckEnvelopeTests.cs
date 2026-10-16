using Lanyard.Shared.AudioClips;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Shared.AudioClips;

[TestClass]
public class DuckEnvelopeTests
{
    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [TestMethod]
    public void StartsAtFullLevel()
    {
        DuckEnvelope envelope = new();

        Assert.AreEqual(1f, envelope.GainAt(Ms(0)));
        Assert.IsFalse(envelope.IsDucked);
    }

    [TestMethod]
    public void DuckTo_FadesInAStraightLineToTheLevel()
    {
        DuckEnvelope envelope = new();

        envelope.DuckTo(0.2f, Ms(500), Ms(1000));

        Assert.AreEqual(1f, envelope.GainAt(Ms(1000)), 0.0001f);
        Assert.AreEqual(0.6f, envelope.GainAt(Ms(1250)), 0.0001f);
        Assert.AreEqual(0.2f, envelope.GainAt(Ms(1500)), 0.0001f);
        Assert.AreEqual(0.2f, envelope.GainAt(Ms(60_000)), 0.0001f);
        Assert.IsTrue(envelope.IsDucked);
    }

    [TestMethod]
    public void Restore_FadesBackUpFromTheDuckedLevel()
    {
        DuckEnvelope envelope = new();
        envelope.DuckTo(0.2f, Ms(500), Ms(0));

        envelope.Restore(Ms(1500), Ms(10_000));

        Assert.AreEqual(0.2f, envelope.GainAt(Ms(10_000)), 0.0001f);
        Assert.AreEqual(0.6f, envelope.GainAt(Ms(10_750)), 0.0001f);
        Assert.AreEqual(1f, envelope.GainAt(Ms(11_500)), 0.0001f);
        Assert.IsFalse(envelope.IsDucked);
    }

    [TestMethod]
    public void Restore_MidFadeDown_StartsFromWhereTheLevelIsWithoutAJump()
    {
        DuckEnvelope envelope = new();
        envelope.DuckTo(0.2f, Ms(500), Ms(0));

        // Halfway down (0.6), a stop arrives.
        envelope.Restore(Ms(1000), Ms(250));

        Assert.AreEqual(0.6f, envelope.GainAt(Ms(250)), 0.0001f);
        Assert.AreEqual(0.8f, envelope.GainAt(Ms(750)), 0.0001f);
        Assert.AreEqual(1f, envelope.GainAt(Ms(1250)), 0.0001f);
    }

    [TestMethod]
    public void DuckTo_WhileAlreadyDucked_MovesToTheNewLevelFromTheCurrentOne()
    {
        DuckEnvelope envelope = new();
        envelope.DuckTo(0.2f, Ms(500), Ms(0));

        // A second clip cuts in with a deeper duck.
        envelope.DuckTo(0f, Ms(200), Ms(5000));

        Assert.AreEqual(0.2f, envelope.GainAt(Ms(5000)), 0.0001f);
        Assert.AreEqual(0f, envelope.GainAt(Ms(5200)), 0.0001f);
        Assert.IsTrue(envelope.IsDucked);
    }

    [TestMethod]
    public void ZeroFade_ChangesStraightAway()
    {
        DuckEnvelope envelope = new();

        envelope.DuckTo(0.3f, TimeSpan.Zero, Ms(100));

        Assert.AreEqual(0.3f, envelope.GainAt(Ms(100)), 0.0001f);
    }

    [TestMethod]
    [DataRow(-0.5f, 0f)]
    [DataRow(1.5f, 1f)]
    public void DuckTo_ClampsTheLevel(float requested, float expected)
    {
        DuckEnvelope envelope = new();

        envelope.DuckTo(requested, TimeSpan.Zero, Ms(0));

        Assert.AreEqual(expected, envelope.GainAt(Ms(1)), 0.0001f);
    }
}
