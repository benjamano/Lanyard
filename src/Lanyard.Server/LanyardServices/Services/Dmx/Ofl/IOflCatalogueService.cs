using Lanyard.Infrastructure.DTO;

namespace Lanyard.Application.Services.Dmx.Ofl;

public interface IOflCatalogueService
{
    // Which OFL commit the bundled snapshot was built from, for the attribution line.
    Result<OflSnapshotInfo> GetSnapshotInfo();

    // Fixtures whose manufacturer + model contain every word of the query. Empty query = no results.
    Result<IReadOnlyList<OflCatalogueEntry>> Search(string? query, int take);

    // The full fixture, parsed, by its OFL key ("cameo/flat-pro-18").
    Result<OflParsedFixture> GetFixture(string oflKey);
}
