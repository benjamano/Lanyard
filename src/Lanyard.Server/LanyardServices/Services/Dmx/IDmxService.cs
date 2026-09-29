using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;

public interface IDmxService
{
    Task<Result<IEnumerable<DmxChannel>>> GetDmxChannelsAsync(Guid clientId);

    /// <summary>Convenience wrapper over <see cref="UpdateChannelValuesAsync"/> for a single channel.</summary>
    Task UpdateChannelValue(Guid clientId, int channelAddress, byte value);

    /// <summary>
    /// Applies a set of channel values in one go: one state update, one
    /// <see cref="OnChannelValuesChanged"/> event and one SignalR message to the kiosk.
    /// Anything that writes more than one channel at a time (scene steps, momentary
    /// release, pushing the virtual desk's buffer live) must use this rather than
    /// looping <see cref="UpdateChannelValue"/>.
    /// </summary>
    Task UpdateChannelValuesAsync(Guid clientId, IReadOnlyList<DmxChannel> channels);

    /// <summary>
    /// Raised once per applied batch (a single ingested fader move is a batch of one).
    /// Subscribers should apply every entry, then re-render once.
    /// </summary>
    event Action<Guid, IReadOnlyList<DmxChannel>>? OnChannelValuesChanged;
}
