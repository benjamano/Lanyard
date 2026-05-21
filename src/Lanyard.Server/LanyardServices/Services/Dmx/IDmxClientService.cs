using Lanyard.Infrastructure.DTO;

public interface IDmxClientService
{
    void SetChannelValue(Guid clientId, int channelAddress, byte value);
}