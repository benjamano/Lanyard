namespace Lanyard.Infrastructure.Models.Dmx;

// Pure channel-snapshot -> color mapping, deliberately free of any DB/service
// dependency so it can be unit tested directly and reused by both the live
// desk preview and the client-side scene-preview scrubber.
public static class DmxFixtureColorMapper
{
    public readonly record struct FixtureColor(byte Red, byte Green, byte Blue, byte Brightness);

    public static FixtureColor GetColor(DmxFixture fixture, IReadOnlyDictionary<int, byte> channelValues)
    {
        byte Get(int offset) => channelValues.TryGetValue(fixture.StartChannel + offset, out byte value) ? value : (byte)0;

        return fixture.FixtureType switch
        {
            DmxFixtureType.Dimmer => new FixtureColor(255, 255, 255, Get(0)),
            DmxFixtureType.Rgb => new FixtureColor(Get(0), Get(1), Get(2), 255),
            DmxFixtureType.RgbDimmer => new FixtureColor(Get(0), Get(1), Get(2), Get(3)),
            _ => new FixtureColor(0, 0, 0, 0)
        };
    }
}
