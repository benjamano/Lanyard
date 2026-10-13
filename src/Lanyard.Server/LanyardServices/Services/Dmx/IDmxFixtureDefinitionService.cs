using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Dmx;
using Lanyard.Infrastructure.Models.Dmx;

namespace Lanyard.Application.Services.Dmx;

public interface IDmxFixtureDefinitionService
{
    // The built-in generic fixtures plus this company's imported and custom ones, active only,
    // with modes, channels and colour ranges loaded. Built-ins first.
    Task<Result<IReadOnlyList<DmxFixtureDefinition>>> GetDefinitionsAsync();

    // Copies a fixture from the bundled Open Fixture Library into this company, or returns the
    // copy it already has of the same revision.
    Task<Result<DmxFixtureDefinition>> ImportFromLibraryAsync(string oflKey);

    // The same, for a fixture file downloaded from open-fixture-library.org (which doesn't name
    // its manufacturer, so the user gives it).
    Task<Result<DmxFixtureDefinition>> ImportFromFileAsync(Stream json, string manufacturer);

    // A fixture type built by hand, owned by this company.
    Task<Result<DmxFixtureDefinition>> CreateCustomAsync(CustomFixtureInput input);

    // Hides one of this company's imported or custom types. Refused while any fixture uses it, and
    // for the shared built-ins.
    Task<Result<bool>> DeleteDefinitionAsync(Guid definitionId);
}
