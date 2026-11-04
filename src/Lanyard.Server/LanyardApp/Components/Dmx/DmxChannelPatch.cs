using Lanyard.Infrastructure.Models.Dmx;

namespace Lanyard.App.Components.Dmx;

// What a fixture has on one DMX channel, for labelling the desk's sliders.
public sealed record DmxChannelPatch(Guid FixtureId, string FixtureName, string Label, DmxChannelRole Role, bool IsFine)
{
    // Channel number -> every fixture channel patched there (more than one when double-patched).
    public static Dictionary<int, List<DmxChannelPatch>> Build(IEnumerable<DmxFixture> fixtures)
    {
        Dictionary<int, List<DmxChannelPatch>> patches = [];

        foreach (DmxFixture fixture in fixtures)
        {
            IEnumerable<DmxFixtureModeChannel> channels = fixture.FixtureMode?.Channels
                ?? (IEnumerable<DmxFixtureModeChannel>)[];

            foreach (DmxFixtureModeChannel channel in channels)
            {
                int address = fixture.StartChannel + channel.Offset;

                if (address is < 1 or > 512)
                {
                    continue;
                }

                if (!patches.TryGetValue(address, out List<DmxChannelPatch>? list))
                {
                    list = [];
                    patches[address] = list;
                }

                list.Add(new DmxChannelPatch(fixture.Id, fixture.Name, channel.Label, channel.Role, channel.FineLevel > 0));
            }
        }

        return patches;
    }
}
