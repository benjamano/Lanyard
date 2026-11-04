namespace Lanyard.Infrastructure.Models.Dmx;

// A kind of light (manufacturer + model) and the DMX modes it can run in. Built-in definitions are
// shared by every company (CompanyId null) and never change; definitions imported from the Open
// Fixture Library or built by hand belong to one company, so a later OFL refresh or another
// company's edit can never change the footprint of fixtures that are already patched.
//
// Not CreateAndUpdateBase: that requires a real user, and the built-ins are seeded by a migration.
public class DmxFixtureDefinition : ISharedOrCompanyOwned
{
    public Guid Id { get; set; }
    public int? CompanyId { get; set; }

    public DmxFixtureDefinitionSource Source { get; set; }

    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    // OFL "<manufacturer-key>/<fixture-key>", e.g. "cameo/flat-pro-18", and the fixture's
    // lastModifyDate at import. Null for built-in and custom definitions.
    public string? OflKey { get; set; }
    public string? OflRevision { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreateDate { get; set; }
    public string? CreateByUserId { get; set; }

    public ICollection<DmxFixtureMode> Modes { get; set; } = [];
}
