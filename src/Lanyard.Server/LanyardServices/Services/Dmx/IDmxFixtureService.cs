using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;

public interface IDmxFixtureService
{
    Task<Result<IEnumerable<DmxFixture>>> GetFixturesForClientAsync(Guid clientId);
    Task<Result<DmxFixture>> CreateFixtureAsync(Guid clientId, string name, int startChannel, DmxFixtureType fixtureType);
    Task<Result<bool>> UpdateFixtureAsync(DmxFixture fixture);
    Task<Result<bool>> DeleteFixtureAsync(Guid fixtureId);
}
