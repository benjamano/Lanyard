using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Dmx.Ofl;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Dmx;

// Fixture definitions a company can patch: the shared built-ins plus its own copies of Open
// Fixture Library fixtures. A copy is per company and per OFL revision, so refreshing the bundled
// snapshot never changes the footprint of a fixture that's already patched.
public class DmxFixtureDefinitionService(
    IDbContextFactory<ApplicationDbContext> factory,
    IOflCatalogueService catalogue,
    IOflFixtureParser parser,
    ISecurityService securityService,
    ILogger<DmxFixtureDefinitionService> logger) : IDmxFixtureDefinitionService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IOflCatalogueService _catalogue = catalogue;
    private readonly IOflFixtureParser _parser = parser;
    private readonly ISecurityService _securityService = securityService;
    private readonly ILogger<DmxFixtureDefinitionService> _logger = logger;

    public async Task<Result<IReadOnlyList<DmxFixtureDefinition>>> GetDefinitionsAsync()
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            List<DmxFixtureDefinition> definitions = await context.DmxFixtureDefinitions
                .AsNoTracking()
                .TagWithCallSite()
                .Include(d => d.Modes)
                    .ThenInclude(m => m.Channels)
                        .ThenInclude(c => c.ColorRanges)
                .Where(d => d.IsActive)
                .OrderBy(d => d.Source)
                .ThenBy(d => d.Manufacturer)
                .ThenBy(d => d.Model)
                .AsSplitQuery()
                .ToListAsync();

            return Result<IReadOnlyList<DmxFixtureDefinition>>.Ok(definitions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving DMX fixture definitions");
            return Result<IReadOnlyList<DmxFixtureDefinition>>.Fail("An error occurred while retrieving fixture types.");
        }
    }

    public async Task<Result<DmxFixtureDefinition>> ImportFromLibraryAsync(string oflKey)
    {
        if (string.IsNullOrWhiteSpace(oflKey))
        {
            return Result<DmxFixtureDefinition>.Fail("Choose a fixture from the library first.");
        }

        Result<OflParsedFixture> parsed = _catalogue.GetFixture(oflKey);

        if (!parsed.IsSuccess || parsed.Data == null)
        {
            return Result<DmxFixtureDefinition>.Fail(parsed.Error ?? "That fixture isn't in the library.");
        }

        return await ImportAsync(parsed.Data, oflKey);
    }

    public async Task<Result<DmxFixtureDefinition>> ImportFromFileAsync(Stream json, string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            return Result<DmxFixtureDefinition>.Fail("Enter the fixture's manufacturer.");
        }

        Result<OflParsedFixture> parsed = _parser.Parse(json, null, manufacturer);

        if (!parsed.IsSuccess || parsed.Data == null)
        {
            return Result<DmxFixtureDefinition>.Fail(parsed.Error ?? "That file couldn't be read.");
        }

        return await ImportAsync(parsed.Data, null);
    }

    private async Task<Result<DmxFixtureDefinition>> ImportAsync(OflParsedFixture fixture, string? oflKey)
    {
        List<OflParsedMode> supported = fixture.Modes.Where(m => m.IsSupported).ToList();

        if (supported.Count == 0)
        {
            return Result<DmxFixtureDefinition>.Fail($"None of the {fixture.Manufacturer} {fixture.Model}'s modes can be used yet. Pixel (matrix) modes aren't supported.");
        }

        try
        {
            Result<string> currentUser = await _securityService.GetCurrentUserIdAsync();

            if (!currentUser.IsSuccess || currentUser.Data == null)
            {
                return Result<DmxFixtureDefinition>.Fail("Unable to determine the current user.");
            }

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            // The tenant filter limits this to the company's own copies (and the built-ins, which
            // are never Source = Ofl).
            IQueryable<DmxFixtureDefinition> existingQuery = context.DmxFixtureDefinitions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(d => d.IsActive && d.Source == DmxFixtureDefinitionSource.Ofl && d.OflRevision == fixture.Revision);

            existingQuery = oflKey != null
                ? existingQuery.Where(d => d.OflKey == oflKey)
                : existingQuery.Where(d => d.OflKey == null && d.Manufacturer == fixture.Manufacturer && d.Model == fixture.Model);

            DmxFixtureDefinition? existing = await existingQuery
                .Include(d => d.Modes)
                    .ThenInclude(m => m.Channels)
                        .ThenInclude(c => c.ColorRanges)
                .AsSplitQuery()
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                return Result<DmxFixtureDefinition>.Ok(existing);
            }

            DmxFixtureDefinition definition = ToDefinition(fixture, oflKey, supported, currentUser.Data);

            context.DmxFixtureDefinitions.Add(definition);
            await context.SaveChangesAsync();

            _logger.LogInformation("Imported fixture {Manufacturer} {Model} ({OflKey}, revision {Revision}) with {ModeCount} modes",
                fixture.Manufacturer, fixture.Model, oflKey ?? "uploaded file", fixture.Revision, supported.Count);

            return Result<DmxFixtureDefinition>.Ok(definition);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing fixture {OflKey}", oflKey ?? fixture.Model);
            return Result<DmxFixtureDefinition>.Fail("An error occurred while adding that fixture type.");
        }
    }

    private static DmxFixtureDefinition ToDefinition(OflParsedFixture fixture, string? oflKey, IReadOnlyList<OflParsedMode> modes, string userId)
    {
        Guid definitionId = Guid.NewGuid();

        return new DmxFixtureDefinition
        {
            Id = definitionId,
            Source = DmxFixtureDefinitionSource.Ofl,
            Manufacturer = fixture.Manufacturer,
            Model = fixture.Model,
            OflKey = oflKey,
            OflRevision = fixture.Revision,
            IsActive = true,
            CreateDate = DateTime.UtcNow,
            CreateByUserId = userId,
            Modes = modes.Select((mode, index) =>
            {
                Guid modeId = Guid.NewGuid();

                return new DmxFixtureMode
                {
                    Id = modeId,
                    DefinitionId = definitionId,
                    Name = mode.Name,
                    ShortName = mode.ShortName,
                    ChannelCount = mode.ChannelCount,
                    SortOrder = index,
                    Channels = mode.Channels.Select(channel =>
                    {
                        Guid channelId = Guid.NewGuid();

                        return new DmxFixtureModeChannel
                        {
                            Id = channelId,
                            ModeId = modeId,
                            Offset = channel.Offset,
                            Label = channel.Label,
                            Role = channel.Role,
                            CoarseOffset = channel.CoarseOffset,
                            FineLevel = channel.FineLevel,
                            DefaultValue = channel.DefaultValue,
                            ColorRanges = channel.ColorRanges.Select(range => new DmxFixtureModeChannelColorRange
                            {
                                Id = Guid.NewGuid(),
                                ModeChannelId = channelId,
                                DmxStart = range.DmxStart,
                                DmxEnd = range.DmxEnd,
                                ColorHex = range.ColorHex
                            }).ToList()
                        };
                    }).ToList()
                };
            }).ToList()
        };
    }
}
