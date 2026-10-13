using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.DataAccess;

// DMX fixture definitions are "shared or owned": the built-ins (CompanyId null) are visible to every
// company and read-only to all of them; imported and custom definitions belong to one company only.
[TestClass]
public class SharedFixtureDefinitionTenancyTests
{
    private const int CompanyA = 1;
    private const int CompanyB = 2;

    private sealed class SignedInTenant(int? companyId) : ITenantProvider
    {
        public bool IsSystem => false;
        public int? CompanyId => companyId;
    }

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using ApplicationDbContext context = new(options);
        context.Database.EnsureCreated();

        return options;
    }

    private static DmxFixtureDefinition NewDefinition(string model, int? companyId)
    {
        Guid definitionId = Guid.NewGuid();
        Guid modeId = Guid.NewGuid();

        return new DmxFixtureDefinition
        {
            Id = definitionId,
            CompanyId = companyId,
            Source = DmxFixtureDefinitionSource.Custom,
            Manufacturer = "Acme",
            Model = model,
            Modes =
            [
                new DmxFixtureMode
                {
                    Id = modeId,
                    DefinitionId = definitionId,
                    Name = "1-channel",
                    ChannelCount = 1,
                    Channels = [new DmxFixtureModeChannel { Id = Guid.NewGuid(), ModeId = modeId, Offset = 0, Label = "Dimmer", Role = DmxChannelRole.Intensity }]
                }
            ]
        };
    }

    [TestMethod]
    public async Task BuiltIns_AreVisibleToEveryCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using ApplicationDbContext asA = new(options, new SignedInTenant(CompanyA));
        await using ApplicationDbContext asB = new(options, new SignedInTenant(CompanyB));

        Assert.AreEqual(3, await asA.DmxFixtureDefinitions.CountAsync());
        Assert.AreEqual(3, await asB.DmxFixtureModes.CountAsync());
        Assert.AreEqual(8, await asB.DmxFixtureModeChannels.CountAsync());
    }

    [TestMethod]
    public async Task CompanyDefinitions_AreInvisibleToOtherCompanies_IncludingTheirModesAndChannels()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using (ApplicationDbContext asA = new(options, new SignedInTenant(CompanyA)))
        {
            asA.DmxFixtureDefinitions.Add(NewDefinition("A's custom wash", companyId: null));
            await asA.SaveChangesAsync();
        }

        await using ApplicationDbContext readAsA = new(options, new SignedInTenant(CompanyA));
        await using ApplicationDbContext readAsB = new(options, new SignedInTenant(CompanyB));

        Assert.AreEqual(1, await readAsA.DmxFixtureDefinitions.CountAsync(d => d.CompanyId == CompanyA), "An insert without a company is stamped with the tenant's.");
        Assert.AreEqual(0, await readAsB.DmxFixtureDefinitions.CountAsync(d => d.Model == "A's custom wash"));
        Assert.AreEqual(3, await readAsB.DmxFixtureModes.CountAsync(), "B sees only the built-in modes.");
        Assert.AreEqual(8, await readAsB.DmxFixtureModeChannels.CountAsync(), "B sees only the built-in channels.");
    }

    [TestMethod]
    public async Task Tenant_CannotCreateADefinitionInAnotherCompany()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using ApplicationDbContext asA = new(options, new SignedInTenant(CompanyA));
        asA.DmxFixtureDefinitions.Add(NewDefinition("Sneaky", companyId: CompanyB));

        await Assert.ThrowsExactlyAsync<CrossTenantWriteException>(() => asA.SaveChangesAsync());
    }

    [TestMethod]
    public async Task Tenant_CannotChangeOrDeleteABuiltInDefinition()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using (ApplicationDbContext rename = new(options, new SignedInTenant(CompanyA)))
        {
            DmxFixtureDefinition builtIn = await rename.DmxFixtureDefinitions.SingleAsync(d => d.Id == BuiltInDmxFixtureDefinitions.RgbDefinitionId);
            builtIn.Model = "Mine now";

            await Assert.ThrowsExactlyAsync<CrossTenantWriteException>(() => rename.SaveChangesAsync());
        }

        await using (ApplicationDbContext delete = new(options, new SignedInTenant(CompanyA)))
        {
            DmxFixtureDefinition builtIn = await delete.DmxFixtureDefinitions.SingleAsync(d => d.Id == BuiltInDmxFixtureDefinitions.RgbDefinitionId);
            delete.DmxFixtureDefinitions.Remove(builtIn);

            await Assert.ThrowsExactlyAsync<CrossTenantWriteException>(() => delete.SaveChangesAsync());
        }
    }

    [TestMethod]
    public async Task Tenant_CannotChangeABuiltInModesChannels()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using ApplicationDbContext asA = new(options, new SignedInTenant(CompanyA));
        DmxFixtureModeChannel red = await asA.DmxFixtureModeChannels.FirstAsync(c => c.ModeId == BuiltInDmxFixtureDefinitions.RgbModeId && c.Offset == 0);
        red.Role = DmxChannelRole.Blue;

        await Assert.ThrowsExactlyAsync<CrossTenantWriteException>(() => asA.SaveChangesAsync());
    }

    [TestMethod]
    public async Task Tenant_CannotAddAChannelToABuiltInMode()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using ApplicationDbContext asA = new(options, new SignedInTenant(CompanyA));
        asA.DmxFixtureModeChannels.Add(new DmxFixtureModeChannel { Id = Guid.NewGuid(), ModeId = BuiltInDmxFixtureDefinitions.RgbModeId, Offset = 3, Label = "Extra", Role = DmxChannelRole.White });

        await Assert.ThrowsExactlyAsync<CrossTenantWriteException>(() => asA.SaveChangesAsync());
    }

    [TestMethod]
    public async Task Tenant_CanEditTheirOwnDefinition_ButNotAnotherCompanys()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        DmxFixtureDefinition ownedByA = NewDefinition("A wash", CompanyA);

        await using (ApplicationDbContext system = new(options))
        {
            system.DmxFixtureDefinitions.Add(ownedByA);
            await system.SaveChangesAsync();
        }

        await using (ApplicationDbContext asA = new(options, new SignedInTenant(CompanyA)))
        {
            DmxFixtureDefinition definition = await asA.DmxFixtureDefinitions.SingleAsync(d => d.Id == ownedByA.Id);
            definition.Model = "A wash v2";
            await asA.SaveChangesAsync();
        }

        await using ApplicationDbContext asB = new(options, new SignedInTenant(CompanyB));
        DmxFixtureDefinition? seenByB = await asB.DmxFixtureDefinitions.FirstOrDefaultAsync(d => d.Id == ownedByA.Id);

        Assert.IsNull(seenByB);

        // Even an attached copy (bypassing the read filter) can't be written by B.
        asB.DmxFixtureDefinitions.Attach(new DmxFixtureDefinition { Id = ownedByA.Id, CompanyId = CompanyA, Manufacturer = "Acme", Model = "A wash v2" }).Property(d => d.Model).CurrentValue = "Hijacked";

        await Assert.ThrowsExactlyAsync<CrossTenantWriteException>(() => asB.SaveChangesAsync());
    }

    [TestMethod]
    public async Task SystemContext_CanWriteSharedRows()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await using ApplicationDbContext system = new(options);
        system.DmxFixtureDefinitions.Add(NewDefinition("Shared", companyId: null));
        await system.SaveChangesAsync();

        Assert.IsNull((await system.DmxFixtureDefinitions.SingleAsync(d => d.Model == "Shared")).CompanyId, "System inserts aren't stamped into a company.");
    }
}
