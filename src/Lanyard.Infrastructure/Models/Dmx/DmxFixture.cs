namespace Lanyard.Infrastructure.Models.Dmx;

public class DmxFixture : CreateAndUpdateBase
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public string Name { get; set; } = string.Empty;

    // First DMX address (1-512) this fixture occupies; FixtureType determines
    // how many consecutive channels from here belong to it and what each means.
    public int StartChannel { get; set; }

    public DmxFixtureType FixtureType { get; set; }

    public bool IsActive { get; set; } = true;
}
