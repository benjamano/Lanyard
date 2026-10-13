using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Dmx.Ofl;

// The bundled Open Fixture Library snapshot (ofl-snapshot.json.gz, rebuilt by
// src/Utilities/update-ofl-snapshot.py). Read-only and identical for every company, so it's a
// singleton that decompresses and indexes the snapshot once, on first use. Nothing here touches
// the database: a fixture only becomes a DmxFixtureDefinition when a company patches it.
public sealed class OflCatalogueService : IOflCatalogueService, IDisposable
{
    public const string SnapshotResourceName = "Lanyard.Services.Services.Dmx.Ofl.ofl-snapshot.json.gz";

    private readonly IOflFixtureParser _parser;
    private readonly ILogger<OflCatalogueService> _logger;
    private readonly Lazy<Catalogue?> _catalogue;

    public OflCatalogueService(IOflFixtureParser parser, ILogger<OflCatalogueService> logger)
        : this(parser, logger, () => Assembly.GetExecutingAssembly().GetManifestResourceStream(SnapshotResourceName))
    {
    }

    // Tests pass their own snapshot.
    public OflCatalogueService(IOflFixtureParser parser, ILogger<OflCatalogueService> logger, Func<Stream?> openSnapshot)
    {
        _parser = parser;
        _logger = logger;
        _catalogue = new Lazy<Catalogue?>(() => Load(openSnapshot), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Result<OflSnapshotInfo> GetSnapshotInfo()
    {
        Catalogue? catalogue = _catalogue.Value;

        return catalogue == null
            ? Result<OflSnapshotInfo>.Fail("The fixture library isn't available.")
            : Result<OflSnapshotInfo>.Ok(catalogue.Info);
    }

    public Result<IReadOnlyList<OflCatalogueEntry>> Search(string? query, int take)
    {
        Catalogue? catalogue = _catalogue.Value;

        if (catalogue == null)
        {
            return Result<IReadOnlyList<OflCatalogueEntry>>.Fail("The fixture library isn't available.");
        }

        string[] terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .ToArray();

        if (terms.Length == 0)
        {
            return Result<IReadOnlyList<OflCatalogueEntry>>.Ok([]);
        }

        // Every word has to appear in the manufacturer or model; model-name matches rank first.
        List<OflCatalogueEntry> matches = catalogue.Entries
            .Where(e => terms.All(t => e.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => terms.Count(t => e.Model.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(e => e.Manufacturer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Model, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(take, 1, 100))
            .ToList();

        return Result<IReadOnlyList<OflCatalogueEntry>>.Ok(matches);
    }

    public Result<OflParsedFixture> GetFixture(string oflKey)
    {
        Catalogue? catalogue = _catalogue.Value;

        if (catalogue == null)
        {
            return Result<OflParsedFixture>.Fail("The fixture library isn't available.");
        }

        if (!catalogue.EntriesByKey.TryGetValue(oflKey, out OflCatalogueEntry? entry))
        {
            return Result<OflParsedFixture>.Fail("That fixture isn't in the library.");
        }

        // A rebadged light ("SameAsDifferentBrand") uses another fixture's channels under its own name.
        string sourceKey = catalogue.Redirects.GetValueOrDefault(oflKey, oflKey);
        Result<OflParsedFixture> parsed = _parser.Parse(catalogue.Fixtures[sourceKey], oflKey, entry.Manufacturer);

        return parsed.IsSuccess && parsed.Data != null && sourceKey != oflKey
            ? Result<OflParsedFixture>.Ok(parsed.Data with { Model = entry.Model })
            : parsed;
    }

    public void Dispose()
    {
        if (_catalogue.IsValueCreated)
        {
            _catalogue.Value?.Document.Dispose();
        }
    }

    private Catalogue? Load(Func<Stream?> openSnapshot)
    {
        try
        {
            using Stream? compressed = openSnapshot();

            if (compressed == null)
            {
                _logger.LogError("The Open Fixture Library snapshot {ResourceName} isn't embedded in the app", SnapshotResourceName);
                return null;
            }

            using GZipStream json = new(compressed, CompressionMode.Decompress);
            JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;

            Dictionary<string, string> manufacturerNames = new(StringComparer.Ordinal);
            if (root.TryGetProperty("manufacturers", out JsonElement manufacturers))
            {
                foreach (JsonProperty manufacturer in manufacturers.EnumerateObject())
                {
                    if (manufacturer.Value.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                    {
                        manufacturerNames[manufacturer.Name] = name.GetString()!;
                    }
                }
            }

            Dictionary<string, JsonElement> fixtures = new(StringComparer.Ordinal);
            Dictionary<string, string> redirects = new(StringComparer.Ordinal);
            List<(string Key, string Manufacturer, string Model, string Target)> pendingRedirects = [];
            List<OflCatalogueEntry> entries = [];

            foreach (JsonProperty fixture in root.GetProperty("fixtures").EnumerateObject())
            {
                string manufacturerKey = fixture.Name.Split('/')[0];
                string manufacturer = manufacturerNames.GetValueOrDefault(manufacturerKey, manufacturerKey);

                if (fixture.Value.TryGetProperty("redirectTo", out JsonElement redirectTo) && redirectTo.ValueKind == JsonValueKind.String
                    && fixture.Value.TryGetProperty("name", out JsonElement redirectName) && redirectName.ValueKind == JsonValueKind.String)
                {
                    pendingRedirects.Add((fixture.Name, manufacturer, redirectName.GetString()!, redirectTo.GetString()!));
                    continue;
                }

                Result<OflParsedFixture> parsed = _parser.Parse(fixture.Value, fixture.Name, manufacturer);

                if (!parsed.IsSuccess || parsed.Data == null)
                {
                    _logger.LogWarning("Skipping Open Fixture Library fixture {OflKey}: {Error}", fixture.Name, parsed.Error);
                    continue;
                }

                fixtures[fixture.Name] = fixture.Value;
                entries.Add(new OflCatalogueEntry(
                    fixture.Name,
                    parsed.Data.Manufacturer,
                    parsed.Data.Model,
                    parsed.Data.Modes.Select(m => new OflCatalogueModeSummary(m.Name, m.ChannelCount, m.IsSupported)).ToList()));
            }

            Dictionary<string, OflCatalogueEntry> byKey = entries.ToDictionary(e => e.Key, StringComparer.Ordinal);

            foreach ((string key, string manufacturer, string model, string target) in pendingRedirects)
            {
                if (byKey.TryGetValue(target, out OflCatalogueEntry? targetEntry))
                {
                    redirects[key] = target;
                    entries.Add(targetEntry with { Key = key, Manufacturer = manufacturer, Model = model });
                }
            }

            JsonElement source = root.GetProperty("source");
            OflSnapshotInfo info = new(
                source.GetProperty("commit").GetString() ?? string.Empty,
                source.TryGetProperty("builtAt", out JsonElement builtAt) && builtAt.TryGetDateTime(out DateTime built) ? built : null,
                entries.Count);

            _logger.LogInformation("Loaded {FixtureCount} Open Fixture Library fixtures from snapshot {Commit}", entries.Count, info.Commit);

            return new Catalogue(document, info, entries, entries.ToDictionary(e => e.Key, StringComparer.Ordinal), fixtures, redirects);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the Open Fixture Library snapshot");
            return null;
        }
    }

    private sealed record Catalogue(
        JsonDocument Document,
        OflSnapshotInfo Info,
        IReadOnlyList<OflCatalogueEntry> Entries,
        IReadOnlyDictionary<string, OflCatalogueEntry> EntriesByKey,
        IReadOnlyDictionary<string, JsonElement> Fixtures,
        IReadOnlyDictionary<string, string> Redirects);
}
