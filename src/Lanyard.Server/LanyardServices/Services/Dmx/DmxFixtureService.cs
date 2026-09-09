using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services;

public class DmxFixtureService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<DmxFixtureService> logger,
    ISecurityService securityService) : IDmxFixtureService
{
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
                .Where(f => f.ClientId == clientId && f.IsActive)
                .OrderBy(f => f.StartChannel)
                .ToListAsync();

            return Result<IEnumerable<DmxFixture>>.Ok(fixtures);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX fixtures for client {ClientId}", clientId);
            return Result<IEnumerable<DmxFixture>>.Fail("An error occurred while retrieving DMX fixtures.");
        }
    }

    public async Task<Result<DmxFixture>> CreateFixtureAsync(Guid clientId, string name, int startChannel, DmxFixtureType fixtureType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<DmxFixture>.Fail("Fixture name cannot be empty.");
        }

        if (startChannel is < 1 or > 512)
        {
            return Result<DmxFixture>.Fail("Start channel must be between 1 and 512.");
        }

        try
        {
            string currentUserId = await _securityService.GetCurrentUserIdAsync().ContinueWith(x => x.Result.Data!);

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxFixture newFixture = new()
            {
                Name = name,
                ClientId = clientId,
                StartChannel = startChannel,
                FixtureType = fixtureType,
                IsActive = true,
                CreateByUserId = currentUserId,
                CreateDate = DateTime.UtcNow,
            };

            context.DmxFixtures.Add(newFixture);

            await context.SaveChangesAsync();

            return Result<DmxFixture>.Ok(newFixture);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating DMX fixture for client {ClientId}", clientId);
            return Result<DmxFixture>.Fail("An error occurred while creating the DMX fixture.");
        }
    }

    public async Task<Result<bool>> UpdateFixtureAsync(DmxFixture fixture)
    {
        if (string.IsNullOrWhiteSpace(fixture.Name))
        {
            return Result<bool>.Fail("Fixture name cannot be empty.");
        }

        if (fixture.StartChannel is < 1 or > 512)
        {
            return Result<bool>.Fail("Start channel must be between 1 and 512.");
        }

        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxFixture? existingFixture = await context.DmxFixtures
                .TagWithCallSite()
                .Where(f => f.Id == fixture.Id && f.IsActive)
                .FirstOrDefaultAsync();

            if (existingFixture == null)
            {
                return Result<bool>.Fail("Fixture not found.");
            }

            existingFixture.Name = fixture.Name;
            existingFixture.StartChannel = fixture.StartChannel;
            existingFixture.FixtureType = fixture.FixtureType;
            existingFixture.UpdateByUserId = await _securityService.GetCurrentUserIdAsync().ContinueWith(x => x.Result.Data!);
            existingFixture.UpdateDate = DateTime.UtcNow;

            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating DMX fixture {FixtureId}", fixture.Id);
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
}
