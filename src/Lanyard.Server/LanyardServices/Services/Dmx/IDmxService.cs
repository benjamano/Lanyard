using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;

public interface IDmxService
{
    Task<Result<IEnumerable<DmxChannel>>> GetDmxChannelsAsync(Guid clientId);
    Task UpdateChannelValue(Guid clientId, int channelAddress, byte value);

    event Action<Guid, int, byte>? OnChannelValueChanged;
}