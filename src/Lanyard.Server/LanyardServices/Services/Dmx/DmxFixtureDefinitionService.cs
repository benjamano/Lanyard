using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Dmx.Ofl;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using System.Text.RegularExpressions;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Dmx;

// Fixture definitions a company can patch: the shared built-ins plus its own copies of Open
// Fixture Library fixtures. A copy is per company and per OFL revision, so refreshing the bundled
// snapshot never changes the footprint of a fixture that's already patched.
public partial class DmxFixtureDefinitionService(
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

    public const int MaxCustomModes = 16;
    private const int MaxNameLength = 100;
    private const int UniverseSize = 512;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    public async Task<Result<DmxFixtureDefinition>> CreateCustomAsync(CustomFixtureInput input)
    {
        string? validationError = ValidateCustom(input);

        if (validationError != null)
        {
            return Result<DmxFixtureDefinition>.Fail(validationError);
        }

        try
        {
            Result<string> currentUser = await _securityService.GetCurrentUserIdAsync();

            if (!currentUser.IsSuccess || currentUser.Data == null)
            {
                return Result<DmxFixtureDefinition>.Fail("Unable to determine the current user.");
            }

            DmxFixtureDefinition definition = ToCustomDefinition(input, currentUser.Data);

            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            context.DmxFixtureDefinitions.Add(definition);
            await context.SaveChangesAsync();

            _logger.LogInformation("Created custom fixture type {Manufacturer} {Model} with {ModeCount} modes", definition.Manufacturer, definition.Model, definition.Modes.Count);

            return Result<DmxFixtureDefinition>.Ok(definition);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating custom fixture type {Model}", input.Model);
            return Result<DmxFixtureDefinition>.Fail("An error occurred while saving the fixture type.");
        }
    }

    public async Task<Result<bool>> DeleteDefinitionAsync(Guid definitionId)
    {
        try
        {
            await using ApplicationDbContext context = await _factory.CreateDbContextAsync();

            DmxFixtureDefinition? definition = await context.DmxFixtureDefinitions
                .TagWithCallSite()
                .Where(d => d.Id == definitionId && d.IsActive)
                .FirstOrDefaultAsync();

            if (definition == null)
            {
                return Result<bool>.Fail("Fixture type not found.");
            }

            if (definition.CompanyId == null)
            {
                return Result<bool>.Fail("The generic fixture types can't be deleted.");
            }

            List<string> inUseBy = await context.DmxFixtures
                .AsNoTracking()
                .TagWithCallSite()
                .Where(f => f.IsActive && f.FixtureMode!.DefinitionId == definitionId)
                .OrderBy(f => f.Name)
                .Select(f => f.Name)
                .Take(5)
                .ToListAsync();

            if (inUseBy.Count > 0)
            {
                return Result<bool>.Fail($"{definition.Manufacturer} {definition.Model} is still used by {string.Join(", ", inUseBy)}. Change or delete those fixtures first.");
            }

            definition.IsActive = false;
            await context.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting fixture type {DefinitionId}", definitionId);
            return Result<bool>.Fail("An error occurred while deleting the fixture type.");
        }
    }

    // Null when valid; otherwise a message for the user.
    private static string? ValidateCustom(CustomFixtureInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Manufacturer) || string.IsNullOrWhiteSpace(input.Model))
        {
            return "Enter a manufacturer and a model.";
        }

        if (input.Manufacturer.Trim().Length > MaxNameLength || input.Model.Trim().Length > MaxNameLength)
        {
            return $"The manufacturer and model can be at most {MaxNameLength} characters.";
        }

        if (input.Modes.Count is 0 or > MaxCustomModes)
        {
            return $"A fixture type needs between 1 and {MaxCustomModes} modes.";
        }

        if (input.Modes.Select(m => m.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.Modes.Count)
        {
            return "Each mode needs a different name.";
        }

        foreach (CustomFixtureModeInput mode in input.Modes)
        {
            if (string.IsNullOrWhiteSpace(mode.Name) || mode.Name.Trim().Length > MaxNameLength)
            {
                return "Every mode needs a name.";
            }

            if (mode.Channels.Count is 0 or > UniverseSize)
            {
                return $"The {mode.Name.Trim()} mode needs between 1 and {UniverseSize} channels.";
            }

            if (mode.Channels.Any(c => string.IsNullOrWhiteSpace(c.Label) || c.Label.Trim().Length > MaxNameLength))
            {
                return $"Every channel in the {mode.Name.Trim()} mode needs a name.";
            }

            if (mode.Channels[0].IsFine)
            {
                return $"The first channel of the {mode.Name.Trim()} mode can't be a fine channel; a fine channel refines the one above it.";
            }

            foreach (CustomFixtureChannelInput channel in mode.Channels)
            {
                if (!Enum.IsDefined(channel.Role))
                {
                    return $"Channel \"{channel.Label.Trim()}\" has an unknown role.";
                }

                if (channel.ColorRanges.Count > 256)
                {
                    return $"Channel \"{channel.Label.Trim()}\" has too many colour ranges.";
                }

                if (channel.ColorRanges.Any(r => r.DmxStart > r.DmxEnd || !HexColor().IsMatch(r.ColorHex ?? string.Empty)))
                {
                    return $"Channel \"{channel.Label.Trim()}\" has a colour range that isn't valid. Each needs a start no higher than its end, and a colour.";
                }
            }
        }

        return null;
    }

    private static DmxFixtureDefinition ToCustomDefinition(CustomFixtureInput input, string userId)
    {
        Guid definitionId = Guid.NewGuid();

        return new DmxFixtureDefinition
        {
            Id = definitionId,
            Source = DmxFixtureDefinitionSource.Custom,
            Manufacturer = input.Manufacturer.Trim(),
            Model = input.Model.Trim(),
            IsActive = true,
            CreateDate = DateTime.UtcNow,
            CreateByUserId = userId,
            Modes = input.Modes.Select((mode, modeIndex) =>
            {
                Guid modeId = Guid.NewGuid();
                List<DmxFixtureModeChannel> channels = [];
                int? lastCoarseOffset = null;
                DmxChannelRole lastCoarseRole = DmxChannelRole.Generic;
                byte fineLevel = 0;

                for (int offset = 0; offset < mode.Channels.Count; offset++)
                {
                    CustomFixtureChannelInput channelInput = mode.Channels[offset];
                    Guid channelId = Guid.NewGuid();
                    bool usesRanges = channelInput.Role is DmxChannelRole.ColorWheel or DmxChannelRole.ColorMacro or DmxChannelRole.Strobe;

                    if (channelInput.IsFine && lastCoarseOffset != null)
                    {
                        fineLevel++;
                    }
                    else
                    {
                        lastCoarseOffset = offset;
                        lastCoarseRole = channelInput.Role;
                        fineLevel = 0;
                    }

                    channels.Add(new DmxFixtureModeChannel
                    {
                        Id = channelId,
                        ModeId = modeId,
                        Offset = offset,
                        Label = channelInput.Label.Trim(),
                        Role = fineLevel > 0 ? lastCoarseRole : channelInput.Role,
                        CoarseOffset = fineLevel > 0 ? lastCoarseOffset : null,
                        FineLevel = fineLevel,
                        ColorRanges = !usesRanges || fineLevel > 0
                            ? []
                            : channelInput.ColorRanges.Select(range => new DmxFixtureModeChannelColorRange
                            {
                                Id = Guid.NewGuid(),
                                ModeChannelId = channelId,
                                DmxStart = range.DmxStart,
                                DmxEnd = range.DmxEnd,
                                ColorHex = range.ColorHex.ToUpperInvariant()
                            }).ToList()
                    });
                }

                return new DmxFixtureMode
                {
                    Id = modeId,
                    DefinitionId = definitionId,
                    Name = mode.Name.Trim(),
                    ChannelCount = channels.Count,
                    SortOrder = modeIndex,
                    Channels = channels
                };
            }).ToList()
        };
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
