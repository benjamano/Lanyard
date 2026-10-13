namespace Lanyard.Infrastructure.Models.Dmx;

public class DmxFixture : CreateAndUpdateBase, ICompanyOwned
{
    public Guid Id { get; set; }
    public int CompanyId { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    public string Name { get; set; } = string.Empty;

    // First DMX address (1-512) this fixture occupies; FixtureMode.ChannelCount determines how many
    // consecutive channels from here belong to it, and FixtureMode.Channels what each one means.
    public int StartChannel { get; set; }

    public Guid FixtureModeId { get; set; }
    public DmxFixtureMode? FixtureMode { get; set; }

    // Superseded by FixtureMode. Still written for the three built-in modes so an app rollback keeps
    // rendering these fixtures; nothing reads it. To be dropped in a later release.
    [Obsolete("Use FixtureMode. Only kept so an app rollback still works; will be dropped.")]
    public DmxFixtureType? FixtureType { get; set; }

    public bool IsActive { get; set; } = true;
}
