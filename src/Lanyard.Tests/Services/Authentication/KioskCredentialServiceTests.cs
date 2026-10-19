using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Authentication;

// SQLite in memory rather than EF InMemory: LastUsedUtc is written with ExecuteUpdateAsync.
[TestClass]
public class KioskCredentialServiceTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<ApplicationDbContext> _options = null!;
    private TestClock _clock = null!;
    private MemoryCache _cache = null!;

    private const int CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId;
    private const int InactiveCompanyId = 2;

    [TestInitialize]
    public async Task Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        await using ApplicationDbContext ctx = new(_options);
        await ctx.Database.EnsureCreatedAsync();

        ctx.Companies.Add(new Company { Id = CompanyId, Name = "Play2Day" });
        ctx.Companies.Add(new Company { Id = InactiveCompanyId, Name = "Closed", IsActive = false });
        await ctx.SaveChangesAsync();

        _clock = new TestClock(new DateTime(2026, 10, 19, 9, 0, 0));
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _cache.Dispose();
        await _connection.DisposeAsync();
    }

    private KioskCredentialService GetService()
    {
        Mock<ISystemDbContextFactory> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(_options));

        return new KioskCredentialService(factory.Object, _cache, _clock, NullLogger<KioskCredentialService>.Instance);
    }

    private async Task<Guid> AddClientAsync(int companyId = CompanyId)
    {
        await using ApplicationDbContext ctx = new(_options);

        Client client = new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Kiosk" };
        ctx.Clients.Add(client);
        await ctx.SaveChangesAsync();

        return client.Id;
    }

    private async Task<IssuedKioskCredential> IssueAsync(Guid clientId)
    {
        Result<IssuedKioskCredential> issued = await GetService().IssueAsync(clientId, ClientCredentialSource.Pairing, "admin-1");

        Assert.IsTrue(issued.IsSuccess, issued.Error);

        return issued.Data!;
    }

    [TestMethod]
    public async Task Issue_StoresOnlyAHashOfTheSecret()
    {
        Guid clientId = await AddClientAsync();

        IssuedKioskCredential issued = await IssueAsync(clientId);

        Assert.IsTrue(KioskCredentialService.TryParseToken(issued.RawToken, out Guid id, out string secret));
        Assert.AreEqual(issued.CredentialId, id);

        await using ApplicationDbContext ctx = new(_options);
        ClientCredential stored = await ctx.ClientCredentials.SingleAsync();

        Assert.AreNotEqual(secret, stored.SecretHash);
        Assert.IsFalse(stored.SecretHash.Contains(secret, StringComparison.Ordinal));
        Assert.AreEqual(KioskCredentialService.HashSecret(secret), stored.SecretHash);
        Assert.AreEqual("admin-1", stored.CreatedByUserId);
        Assert.IsNotNull(stored.ActivatedUtc);
    }

    [TestMethod]
    public async Task Issue_FailsForAKioskThatDoesNotExist()
    {
        Result<IssuedKioskCredential> issued = await GetService().IssueAsync(Guid.NewGuid(), ClientCredentialSource.Pairing, null);

        Assert.IsFalse(issued.IsSuccess);
    }

    [TestMethod]
    public async Task Validate_AcceptsTheIssuedToken()
    {
        Guid clientId = await AddClientAsync();
        IssuedKioskCredential issued = await IssueAsync(clientId);

        Result<KioskIdentity> result = await GetService().ValidateAsync(issued.RawToken);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(clientId, result.Data!.ClientId);
        Assert.AreEqual(CompanyId, result.Data.CompanyId);
        Assert.AreEqual(issued.CredentialId, result.Data.CredentialId);
    }

    [TestMethod]
    public async Task Validate_RecordsWhenTheCredentialWasLastUsed()
    {
        Guid clientId = await AddClientAsync();
        IssuedKioskCredential issued = await IssueAsync(clientId);

        await GetService().ValidateAsync(issued.RawToken);

        await using ApplicationDbContext ctx = new(_options);
        Assert.AreEqual(_clock.UtcNow, (await ctx.ClientCredentials.SingleAsync()).LastUsedUtc);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not-a-token")]
    [DataRow("lk1.not-a-guid.secret")]
    [DataRow("lk2.0123456789abcdef0123456789abcdef.secret")]
    [DataRow("lk1.0123456789abcdef0123456789abcdef.")]
    [DataRow("lk1.0123456789abcdef0123456789abcdef.secret.extra")]
    public async Task Validate_RejectsMalformedTokens(string? token)
    {
        Result<KioskIdentity> result = await GetService().ValidateAsync(token);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Validate_RejectsAnUnknownCredentialId()
    {
        Result<KioskIdentity> result = await GetService().ValidateAsync(KioskCredentialService.FormatToken(Guid.NewGuid(), "secret"));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Validate_RejectsTheRightIdWithTheWrongSecret()
    {
        Guid clientId = await AddClientAsync();
        IssuedKioskCredential issued = await IssueAsync(clientId);

        Result<KioskIdentity> result = await GetService().ValidateAsync(KioskCredentialService.FormatToken(issued.CredentialId, "guessed"));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Validate_RejectsTheWrongSecret_EvenWhenTheCredentialIsCached()
    {
        Guid clientId = await AddClientAsync();
        IssuedKioskCredential issued = await IssueAsync(clientId);
        KioskCredentialService service = GetService();

        Assert.IsTrue((await service.ValidateAsync(issued.RawToken)).IsSuccess);

        Result<KioskIdentity> result = await service.ValidateAsync(KioskCredentialService.FormatToken(issued.CredentialId, "guessed"));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Issue_RevokesTheKiosksPreviousCredential()
    {
        Guid clientId = await AddClientAsync();
        IssuedKioskCredential first = await IssueAsync(clientId);
        KioskCredentialService service = GetService();

        // Cached as valid before the new one is issued.
        Assert.IsTrue((await service.ValidateAsync(first.RawToken)).IsSuccess);

        IssuedKioskCredential second = await IssueAsync(clientId);

        Assert.IsFalse((await service.ValidateAsync(first.RawToken)).IsSuccess, "The replaced credential still worked.");
        Assert.IsTrue((await service.ValidateAsync(second.RawToken)).IsSuccess);

        await using ApplicationDbContext ctx = new(_options);
        ClientCredential old = await ctx.ClientCredentials.SingleAsync(x => x.Id == first.CredentialId);
        Assert.IsFalse(old.IsActive);
        Assert.IsNotNull(old.RevokedUtc);
    }

    [TestMethod]
    public async Task Validate_RejectsACredentialAwaitingConfirmation()
    {
        Guid clientId = await AddClientAsync();
        IssuedKioskCredential issued = await IssueAsync(clientId);

        await using (ApplicationDbContext ctx = new(_options))
        {
            ClientCredential row = await ctx.ClientCredentials.SingleAsync();
            row.ActivatedUtc = null;
            await ctx.SaveChangesAsync();
        }

        Result<KioskIdentity> result = await GetService().ValidateAsync(issued.RawToken);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Validate_RejectsAKioskWhoseCompanyIsNotActive()
    {
        Guid clientId = await AddClientAsync(InactiveCompanyId);
        IssuedKioskCredential issued = await IssueAsync(clientId);

        Result<KioskIdentity> result = await GetService().ValidateAsync(issued.RawToken);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task ValidateById_FollowsRevocation()
    {
        Guid clientId = await AddClientAsync();
        IssuedKioskCredential first = await IssueAsync(clientId);
        KioskCredentialService service = GetService();

        Assert.IsTrue((await service.ValidateByIdAsync(first.CredentialId)).IsSuccess);

        await IssueAsync(clientId);

        Assert.IsFalse((await service.ValidateByIdAsync(first.CredentialId)).IsSuccess);
    }

    [TestMethod]
    public async Task GetClientCompanyId_FindsKnownKiosks_Only()
    {
        Guid clientId = await AddClientAsync();

        Assert.AreEqual(CompanyId, (await GetService().GetClientCompanyIdAsync(clientId)).Data);
        Assert.IsFalse((await GetService().GetClientCompanyIdAsync(Guid.NewGuid())).IsSuccess);
    }

    [TestMethod]
    public async Task Validate_ReportsADatabaseFailure_AsUnavailable_NotAsAnInvalidCredential()
    {
        Mock<ISystemDbContextFactory> broken = new();
        broken.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database is down"));

        KioskCredentialService service = new(broken.Object, _cache, _clock, NullLogger<KioskCredentialService>.Instance);

        Result<KioskIdentity> result = await service.ValidateAsync(KioskCredentialService.FormatToken(Guid.NewGuid(), "secret"));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(KioskCredentialService.CheckUnavailableError, result.Error);
    }

    [TestMethod]
    public async Task Rotation_NewCredentialOnlyWorksOnceConfirmed_ThenTheOldOneStops()
    {
        Guid clientId = await AddClientAsync();
        IssuedKioskCredential old = await IssueAsync(clientId);
        KioskCredentialService service = GetService();

        Result<IssuedKioskCredential> pending = await service.BeginRotationAsync(clientId, "admin-1");
        Assert.IsTrue(pending.IsSuccess, pending.Error);

        Assert.IsFalse((await service.ValidateAsync(pending.Data!.RawToken)).IsSuccess, "A rotation that the kiosk hasn't confirmed must not work yet.");
        Assert.IsTrue((await service.ValidateAsync(old.RawToken)).IsSuccess, "The old credential must keep working until the kiosk confirms.");

        Assert.IsTrue((await service.CompleteRotationAsync(pending.Data.CredentialId, "admin-1")).IsSuccess);

        Assert.IsTrue((await service.ValidateAsync(pending.Data.RawToken)).IsSuccess);
        Assert.IsFalse((await service.ValidateAsync(old.RawToken)).IsSuccess, "The old credential still worked after the rotation.");
    }

    [TestMethod]
    public async Task Rotation_Cancelled_KeepsTheOldCredential()
    {
        Guid clientId = await AddClientAsync();
        IssuedKioskCredential old = await IssueAsync(clientId);
        KioskCredentialService service = GetService();

        IssuedKioskCredential pending = (await service.BeginRotationAsync(clientId, "admin-1")).Data!;
        Assert.IsTrue((await service.CancelRotationAsync(pending.CredentialId, "The kiosk didn't confirm in time")).Data);

        Assert.IsTrue((await service.ValidateAsync(old.RawToken)).IsSuccess);
        Assert.IsFalse((await service.ValidateAsync(pending.RawToken)).IsSuccess);
        Assert.IsFalse((await service.CompleteRotationAsync(pending.CredentialId, "admin-1")).IsSuccess, "A cancelled rotation can't be completed later.");
    }

    [TestMethod]
    public async Task Rotation_NeedsAnActiveCredential()
    {
        Guid clientId = await AddClientAsync();

        Assert.IsFalse((await GetService().BeginRotationAsync(clientId, "admin-1")).IsSuccess);
    }

    [TestMethod]
    public async Task RevokeAll_StopsTheCredential_EvenWhileItIsCached()
    {
        Guid clientId = await AddClientAsync();
        IssuedKioskCredential issued = await IssueAsync(clientId);
        KioskCredentialService service = GetService();

        Assert.IsTrue((await service.ValidateAsync(issued.RawToken)).IsSuccess);

        Result<int> revoked = await service.RevokeAllAsync(clientId, "admin-1", "PC stolen");

        Assert.AreEqual(1, revoked.Data);
        Assert.IsFalse((await service.ValidateAsync(issued.RawToken)).IsSuccess);
        Assert.IsFalse((await service.ValidateByIdAsync(issued.CredentialId)).IsSuccess);

        await using ApplicationDbContext ctx = new(_options);
        ClientCredential row = await ctx.ClientCredentials.SingleAsync();
        Assert.AreEqual("PC stolen", row.RevokeReason);
        Assert.AreEqual("admin-1", row.RevokedByUserId);
    }
}
