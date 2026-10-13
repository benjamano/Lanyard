using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.Models.Dmx;

public interface IDmxFixtureService
{
    // Active fixtures with FixtureMode, its Channels and their ColorRanges loaded.
    Task<Result<IEnumerable<DmxFixture>>> GetFixturesForClientAsync(Guid clientId);

    // Fails when the fixture would run past channel 512, or when it shares channels with another
    // fixture and allowOverlap is false (call GetOverlapsAsync first to ask the user).
    Task<Result<DmxFixture>> CreateFixtureAsync(Guid clientId, string name, int startChannel, Guid fixtureModeId, bool allowOverlap);

    // Same checks as create, but only when the start channel or mode changes, so renaming a
    // fixture that already overlaps (or predates these checks) still works.
    Task<Result<bool>> UpdateFixtureAsync(Guid fixtureId, string name, int startChannel, Guid fixtureModeId, bool allowOverlap);

    Task<Result<bool>> DeleteFixtureAsync(Guid fixtureId);

    Task<Result<IReadOnlyList<DmxFixtureOverlap>>> GetOverlapsAsync(Guid clientId, int startChannel, int channelCount, Guid? excludeFixtureId);
}
