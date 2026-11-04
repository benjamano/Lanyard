namespace Lanyard.Infrastructure.DTO.Dmx;

// Another fixture on the same client that shares DMX channels with the one being placed:
// FirstSharedChannel..LastSharedChannel are the channels both use.
public record DmxFixtureOverlap(Guid FixtureId, string FixtureName, int FirstSharedChannel, int LastSharedChannel);
