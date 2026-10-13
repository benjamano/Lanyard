using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class DmxFixtureService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<DmxFixtureService> logger,
    ISecurityService securityService) : IDmxFixtureService
{
    public const int UniverseSize = 512;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<DmxFixtureService> _logger = logger;
    private readonly ISecurityService _securityService = securityService;

    public async Task<Result<IEnumerable<DmxFixture>>> GetFixturesForClientAsync(Guid clientId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<DmxFixture> fixtures = await context.DmxFixtures
                .AsNoTracking()
                .TagWithCallSite()
                .Include(f => f.FixtureMode)
                    .ThenInclude(m => m!.Definition)
                .Include(f => f.FixtureMode)
                    .ThenInclude(m => m!.Channels)
                        .ThenInclude(c => c.ColorRanges)
                .Where(f => f.ClientId == clientId && f.IsActive)
                .OrderBy(f => f.StartChannel)
                .AsSplitQuery()
                .ToListAsync();

            return Result<IEnumerable<DmxFixture>>.Ok(fixtures);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX fixtures for client {ClientId}", clientId);
            return Result<IEnumerable<DmxFixture>>.Fail("An error occurred while retrieving DMX fixtures.");
        }
    }

    public async Task<Result<DmxFixture>> CreateFixtureAsync(Guid clientId, string name, int startChannel, Guid fixtureModeId, bool allowOverlap)
    {
        string trimmedName = name?.Trim() ?? string.Empty;

        if (trimmedName.Length == 0)
        {
            return Result<DmxFixture>.Fail("Fixture name cannot be empty.");
        }

        if (startChannel is < 1 or > UniverseSize)
        {
            return Result<DmxFixture>.Fail($"Start channel must be between 1 and {UniverseSize}.");
        }

        try
        {
            Result<string> currentUserResult = await _securityService.GetCurrentUserIdAsync();

            if (!currentUserResult.IsSuccess || currentUserResult.Data == null)
            {
                return Result<DmxFixture>.Fail("Unable to determine the current user.");
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxFixtureMode? mode = await FindModeAsync(context, fixtureModeId);

            if (mode == null)
            {
                return Result<DmxFixture>.Fail("That fixture mode wasn't found.");
            }

            string? placementError = await CheckPlacementAsync(context, clientId, startChannel, mode.ChannelCount, null, allowOverlap);

            if (placementError != null)
            {
                return Result<DmxFixture>.Fail(placementError);
            }

            DmxFixture newFixture = new()
            {
                Name = trimmedName,
                ClientId = clientId,
                StartChannel = startChannel,
                FixtureModeId = mode.Id,
#pragma warning disable CS0618 // still written for built-ins so an app rollback renders them
                FixtureType = BuiltInDmxFixtureDefinitions.FixtureTypeFor(mode.Id),
#pragma warning restore CS0618
                IsActive = true,
                CreateByUserId = currentUserResult.Data,
                CreateDate = DateTime.UtcNow,
            };

            context.DmxFixtures.Add(newFixture);

            await context.SaveChangesAsync();

            newFixture.FixtureMode = mode;

            return Result<DmxFixture>.Ok(newFixture);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating DMX fixture for client {ClientId}", clientId);
            return Result<DmxFixture>.Fail("An error occurred while creating the DMX fixture.");
        }
    }

    public async Task<Result<bool>> UpdateFixtureAsync(Guid fixtureId, string name, int startChannel, Guid fixtureModeId, bool allowOverlap)
    {
        string trimmedName = name?.Trim() ?? string.Empty;

        if (trimmedName.Length == 0)
        {
            return Result<bool>.Fail("Fixture name cannot be empty.");
        }

        if (startChannel is < 1 or > UniverseSize)
        {
            return Result<bool>.Fail($"Start channel must be between 1 and {UniverseSize}.");
        }

        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxFixture? existingFixture = await context.DmxFixtures
                .TagWithCallSite()
                .Where(f => f.Id == fixtureId && f.IsActive)
                .FirstOrDefaultAsync();

            if (existingFixture == null)
            {
                return Result<bool>.Fail("Fixture not found.");
            }

            Result<string> currentUserResult = await _securityService.GetCurrentUserIdAsync();

            if (!currentUserResult.IsSuccess || currentUserResult.Data == null)
            {
                return Result<bool>.Fail("Unable to determine the current user.");
            }

            bool placementChanged = existingFixture.StartChannel != startChannel || existingFixture.FixtureModeId != fixtureModeId;

            if (placementChanged)
            {
                DmxFixtureMode? mode = await FindModeAsync(context, fixtureModeId);

                if (mode == null)
                {
                    return Result<bool>.Fail("That fixture mode wasn't found.");
                }

                string? placementError = await CheckPlacementAsync(context, existingFixture.ClientId, startChannel, mode.ChannelCount, existingFixture.Id, allowOverlap);

                if (placementError != null)
                {
                    return Result<bool>.Fail(placementError);
                }

                existingFixture.StartChannel = startChannel;
                existingFixture.FixtureModeId = mode.Id;
#pragma warning disable CS0618 // still written for built-ins so an app rollback renders them
                existingFixture.FixtureType = BuiltInDmxFixtureDefinitions.FixtureTypeFor(mode.Id);
#pragma warning restore CS0618
            }

            existingFixture.Name = trimmedName;
            existingFixture.UpdateByUserId = currentUserResult.Data;
            existingFixture.UpdateDate = DateTime.UtcNow;

            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating DMX fixture {FixtureId}", fixtureId);
            return Result<bool>.Fail("An error occurred while updating the DMX fixture.");
        }
    }

    public async Task<Result<bool>> DeleteFixtureAsync(Guid fixtureId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxFixture? fixture = await context.DmxFixtures
                .TagWithCallSite()
                .Where(f => f.Id == fixtureId && f.IsActive)
                .FirstOrDefaultAsync();

            if (fixture == null)
            {
                return Result<bool>.Fail("Fixture not found.");
            }

            fixture.IsActive = false;

            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting DMX fixture {FixtureId}", fixtureId);
            return Result<bool>.Fail("An error occurred while deleting the DMX fixture.");
        }
    }

    public async Task<Result<IReadOnlyList<DmxFixtureOverlap>>> GetOverlapsAsync(Guid clientId, int startChannel, int channelCount, Guid? excludeFixtureId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            IReadOnlyList<DmxFixtureOverlap> overlaps = await FindOverlapsAsync(context, clientId, startChannel, channelCount, excludeFixtureId);

            return Result<IReadOnlyList<DmxFixtureOverlap>>.Ok(overlaps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking DMX fixture overlaps for client {ClientId}", clientId);
            return Result<IReadOnlyList<DmxFixtureOverlap>>.Fail("An error occurred while checking for overlapping fixtures.");
        }
    }

    // Only modes whose definition is still active. The tenant filter already hides other
    // companies' definitions, so a mode id from another company reads as "not found".
    private static Task<DmxFixtureMode?> FindModeAsync(ApplicationDbContext context, Guid fixtureModeId) =>
        context.DmxFixtureModes
            .AsNoTracking()
            .TagWithCallSite()
            .Include(m => m.Definition)
            .Include(m => m.Channels)
                .ThenInclude(c => c.ColorRanges)
            .Where(m => m.Id == fixtureModeId && m.Definition!.IsActive)
            .AsSplitQuery()
            .FirstOrDefaultAsync();

    // Null when the fixture fits; otherwise a message for the user.
    private static async Task<string?> CheckPlacementAsync(ApplicationDbContext context, Guid clientId, int startChannel, int channelCount, Guid? excludeFixtureId, bool allowOverlap)
    {
        int lastChannel = startChannel + channelCount - 1;

        if (lastChannel > UniverseSize)
        {
            return $"This fixture needs channels {startChannel}-{lastChannel}, but the DMX universe ends at {UniverseSize}. Start it at {UniverseSize - channelCount + 1} or lower.";
        }

        if (allowOverlap)
        {
            return null;
        }

        IReadOnlyList<DmxFixtureOverlap> overlaps = await FindOverlapsAsync(context, clientId, startChannel, channelCount, excludeFixtureId);

        if (overlaps.Count == 0)
        {
            return null;
        }

        string details = string.Join(", ", overlaps.Select(o => o.FirstSharedChannel == o.LastSharedChannel
            ? $"{o.FixtureName} (channel {o.FirstSharedChannel})"
            : $"{o.FixtureName} (channels {o.FirstSharedChannel}-{o.LastSharedChannel})"));

        return $"This fixture shares channels with {details}.";
    }

    private static async Task<IReadOnlyList<DmxFixtureOverlap>> FindOverlapsAsync(ApplicationDbContext context, Guid clientId, int startChannel, int channelCount, Guid? excludeFixtureId)
    {
        int lastChannel = startChannel + channelCount - 1;

        List<OverlapCandidate> candidates = await context.DmxFixtures
            .AsNoTracking()
            .TagWithCallSite()
            .Where(f => f.ClientId == clientId && f.IsActive && f.Id != excludeFixtureId)
            .Where(f => f.StartChannel <= lastChannel && f.StartChannel + f.FixtureMode!.ChannelCount - 1 >= startChannel)
            .OrderBy(f => f.StartChannel)
            .Select(f => new OverlapCandidate(f.Id, f.Name, f.StartChannel, f.FixtureMode!.ChannelCount))
            .ToListAsync();

        return candidates
            .Select(f => new DmxFixtureOverlap(
                f.Id,
                f.Name,
                Math.Max(startChannel, f.StartChannel),
                Math.Min(lastChannel, f.StartChannel + f.ChannelCount - 1)))
            .ToList();
    }

    private sealed record OverlapCandidate(Guid Id, string Name, int StartChannel, int ChannelCount);
}
