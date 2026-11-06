using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;

/// <summary>
/// The system-level DMX output engine (a singleton). It does not check who is calling: code
/// acting for a signed-in user must use the scoped <see cref="IDmxDeskService"/> instead, which
/// checks the kiosk belongs to the user's company.
/// </summary>
public interface IDmxService
{
    /// <summary>A copy of all 512 channels as the server last set them (zeros for a kiosk it hasn't driven).</summary>
    Task<Result<IEnumerable<DmxChannel>>> GetDmxChannelsAsync(Guid clientId);

    /// <summary>Convenience wrapper over <see cref="UpdateChannelValuesAsync"/> for a single channel.</summary>
    Task UpdateChannelValue(Guid clientId, int channelAddress, byte value);

    /// <summary>
    /// Applies a set of channel values in one go: one state update and one subscriber
    /// notification. Sends to the kiosk are coalesced (latest value per channel, ~40 Hz), so
    /// anything that writes more than one channel at a time should still pass them together.
    /// Addresses outside 1-512 are ignored.
    /// </summary>
    Task UpdateChannelValuesAsync(Guid clientId, IReadOnlyList<DmxChannel> channels);

    /// <summary>
    /// Calls <paramref name="handler"/> once per applied batch for this kiosk only, on the
    /// writer's thread - Blazor subscribers must marshal with InvokeAsync. Dispose to stop.
    /// </summary>
    IDisposable SubscribeToClient(Guid clientId, Action<IReadOnlyList<DmxChannel>> handler);
}
