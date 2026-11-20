using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Dmx;

// The fixture cards' brightness and colour controls, and that they agree with the colour preview.
[TestClass]
public class DmxFixtureControlsTests
{
    private static DmxFixture Fixture(int start, params DmxChannelRole[] roles) => new()
    {
        Id = Guid.NewGuid(),
        Name = "F",
        CreateByUserId = "test",
        StartChannel = start,
        FixtureMode = new DmxFixtureMode
        {
            Name = "m",
            ChannelCount = roles.Length,
            Channels = roles.Select((role, i) => new DmxFixtureModeChannel { Offset = i, Role = role, Label = role.ToString() }).ToList()
        }
    };

    private static (byte[] values, DmxUniverseView view) Universe()
    {
        byte[] values = new byte[513];
        return (values, new DmxUniverseView(values));
    }

    private static void Apply(byte[] values, IEnumerable<DmxChannel> writes)
    {
        foreach (DmxChannel write in writes)
        {
            values[write.Address] = write.Value;
        }
    }

    [TestMethod]
    public void SetColour_RoundTripsThroughThePreview()
    {
        DmxFixture rgbDimmer = Fixture(10, DmxChannelRole.Red, DmxChannelRole.Green, DmxChannelRole.Blue, DmxChannelRole.Intensity);
        (byte[] values, DmxUniverseView view) = Universe();
        values[13] = 255;

        Apply(values, DmxFixtureControls.SetColour(rgbDimmer, 255, 128, 0));

        Assert.AreEqual(((byte)255, (byte)128, (byte)0), DmxFixtureControls.GetColour(rgbDimmer, view));
        DmxFixtureColorMapper.FixtureColor shown = DmxFixtureColorMapper.GetColor(rgbDimmer, view);
        Assert.AreEqual(255, shown.Red);
        Assert.AreEqual(0, shown.Blue);
        Assert.IsTrue(shown.Green > 0 && shown.Green < shown.Red);
    }

    [TestMethod]
    public void SetColour_OnRgbw_PutsTheGreyPartOnWhite()
    {
        DmxFixture rgbw = Fixture(1, DmxChannelRole.Red, DmxChannelRole.Green, DmxChannelRole.Blue, DmxChannelRole.White);

        Dictionary<int, byte> writes = DmxFixtureControls.SetColour(rgbw, 255, 200, 200).ToDictionary(w => w.Address, w => w.Value);

        Assert.AreEqual((byte)55, writes[1]);
        Assert.AreEqual((byte)0, writes[2]);
        Assert.AreEqual((byte)0, writes[3]);
        Assert.AreEqual((byte)200, writes[4]);
    }

    [TestMethod]
    public void SetColour_OnCmy_Subtracts()
    {
        DmxFixture cmy = Fixture(1, DmxChannelRole.Cyan, DmxChannelRole.Magenta, DmxChannelRole.Yellow);

        Dictionary<int, byte> writes = DmxFixtureControls.SetColour(cmy, 255, 0, 255).ToDictionary(w => w.Address, w => w.Value);

        CollectionAssert.AreEqual(new byte[] { 0, 255, 0 }, new[] { writes[1], writes[2], writes[3] });
    }

    [TestMethod]
    public void SetIntensity_UsesTheDimmerWhenThereIsOne()
    {
        DmxFixture rgbDimmer = Fixture(5, DmxChannelRole.Red, DmxChannelRole.Green, DmxChannelRole.Blue, DmxChannelRole.Intensity);
        (_, DmxUniverseView view) = Universe();

        List<DmxChannel> writes = DmxFixtureControls.SetIntensity(rgbDimmer, view, 128);

        Assert.AreEqual(1, writes.Count);
        Assert.AreEqual(8, writes[0].Address);
        Assert.AreEqual((byte)128, writes[0].Value);
    }

    [TestMethod]
    public void SetIntensity_WithoutADimmer_ScalesTheEmittersKeepingTheHue()
    {
        DmxFixture rgb = Fixture(1, DmxChannelRole.Red, DmxChannelRole.Green, DmxChannelRole.Blue);
        (byte[] values, DmxUniverseView view) = Universe();
        values[1] = 200;
        values[2] = 100;

        Apply(values, DmxFixtureControls.SetIntensity(rgb, view, 100));

        Assert.AreEqual((byte)100, values[1]);
        Assert.AreEqual((byte)50, values[2]);
        Assert.AreEqual((byte)0, values[3]);
        Assert.AreEqual((byte)100, DmxFixtureControls.GetIntensity(rgb, view));
    }

    [TestMethod]
    public void SetIntensity_WithoutADimmer_FromDark_ComesUpWhite()
    {
        DmxFixture rgb = Fixture(1, DmxChannelRole.Red, DmxChannelRole.Green, DmxChannelRole.Blue);
        (byte[] values, DmxUniverseView view) = Universe();

        Apply(values, DmxFixtureControls.SetIntensity(rgb, view, 255));

        Assert.AreEqual(((byte)255, (byte)255, (byte)255), DmxFixtureControls.GetColour(rgb, view));
    }

    [TestMethod]
    public void GetIntensity_IsNullForAFixtureWithNothingToDim()
    {
        DmxFixture scanner = Fixture(1, DmxChannelRole.Pan, DmxChannelRole.Tilt, DmxChannelRole.Gobo);
        (_, DmxUniverseView view) = Universe();

        Assert.IsNull(DmxFixtureControls.GetIntensity(scanner, view));
        Assert.IsFalse(DmxFixtureControls.HasColour(scanner));
    }

    [TestMethod]
    public void FineChannels_AreNotMistakenForTheCoarseOne()
    {
        DmxFixture head = Fixture(1, DmxChannelRole.Pan, DmxChannelRole.Pan);
        head.FixtureMode!.Channels.First(c => c.Offset == 0).FineLevel = 1;

        Assert.AreEqual(2, DmxFixtureControls.AddressOf(head, DmxChannelRole.Pan));
    }

    [TestMethod]
    [DataRow("#ff8000", true)]
    [DataRow("#FF8000", true)]
    [DataRow("ff8000", false)]
    [DataRow("#ff80", false)]
    [DataRow("#gg8000", false)]
    public void TryParseHex(string hex, bool ok)
    {
        Assert.AreEqual(ok, DmxFixtureControls.TryParseHex(hex, out _));
    }

    [TestMethod]
    public void UniverseView_ReadsTheArrayWithoutCopying()
    {
        (byte[] values, DmxUniverseView view) = Universe();
        values[512] = 7;

        Assert.AreEqual((byte)7, view[512]);
        Assert.AreEqual(512, view.Count);
        Assert.IsFalse(view.ContainsKey(0));
        Assert.IsFalse(view.TryGetValue(513, out _));
    }
}
