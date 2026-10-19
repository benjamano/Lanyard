using System.Security.Claims;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Contracts;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Authentication;

[TestClass]
public class KioskEnrolmentServiceTests
{
    private const int SeedCompanyId = ApplicationDbContext.SeedPlay2DayCompanyId;
    private const int CompanyA = 2;
    private const int CompanyB = 3;
    private const int CompanyALocation = 20;
    private const int CompanyBLocation = 30;

    private SqliteConnection _connection = null!;
    private DbContextOptions<ApplicationDbContext> _options = null!;
    private TestClock _clock = null!;
    private MemoryCache _cache = null!;
    private Mock<IClientService> _clientService = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        await using ApplicationDbContext ctx = new(_options);
        await ctx.Database.EnsureCreatedAsync();

        ctx.Companies.AddRange(
            new Company { Id = SeedCompanyId, Name = "Play2Day" },
            new Company { Id = CompanyA, Name = "Company A" },
            new Company { Id = CompanyB, Name = "Company B" });
        ctx.Locations.AddRange(
            new Location { Id = CompanyALocation, CompanyId = CompanyA, Name = "A site", IsActive = true },
            new Location { Id = CompanyBLocation, CompanyId = CompanyB, Name = "B site", IsActive = true });
        await ctx.SaveChangesAsync();

        _clock = new TestClock(new DateTime(2026, 10, 19, 9, 0, 0));
        _cache = new MemoryCache(new MemoryCacheOptions());
        _clientService = new Mock<IClientService>();
        _clientService.Setup(x => x.SetClientLocationAsync(It.IsAny<Guid>(), It.IsAny<int?>())).ReturnsAsync(Result<bool>.Ok(true));
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _cache.Dispose();
        await _connection.DisposeAsync();
    }

    private ISystemDbContextFactory Factory()
    {
        Mock<ISystemDbContextFactory> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(_options));
        return factory.Object;
    }

    private KioskEnrolmentService GetService(int? companyId = CompanyA, string? role = "Admin", string userId = "admin-a")
    {
        Mock<ITenantContext> tenant = new();
        tenant.Setup(x => x.IsSystem).Returns(companyId is null);
        tenant.Setup(x => x.CompanyId).Returns(companyId);

        List<Claim> claims = [new Claim(ClaimTypes.NameIdentifier, userId)];

        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        Mock<AuthenticationStateProvider> auth = new();
        auth.Setup(x => x.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))));

        KioskCredentialService credentials = new(Factory(), _cache, _clock, NullLogger<KioskCredentialService>.Instance);

        return new KioskEnrolmentService(Factory(), credentials, _clientService.Object, tenant.Object, auth.Object, _cache, _clock, NullLogger<KioskEnrolmentService>.Instance);
    }

    private async Task<KioskEnrolmentStartedDTO> StartAsync(Guid? clientId = null)
    {
        Result<KioskEnrolmentStartedDTO> started = await GetService().StartAsync(new KioskEnrolmentRequestDTO(clientId ?? Guid.NewGuid(), "BAR-PC", "1.0.43"), "10.0.0.9");

        Assert.IsTrue(started.IsSuccess, started.Error);

        return started.Data!;
    }

    private async Task<Guid> ClientIdOf(Guid requestId)
    {
        await using ApplicationDbContext ctx = new(_options);
        return (await ctx.ClientEnrolmentRequests.SingleAsync(x => x.Id == requestId)).ClientId;
    }

    private async Task AddClientAsync(Guid clientId, int companyId, string name = "Existing kiosk")
    {
        await using ApplicationDbContext ctx = new(_options);
        ctx.Clients.Add(new Client { Id = clientId, CompanyId = companyId, Name = name, CreateDate = _clock.UtcNow, MusicCacheLimitMb = 900 });
        await ctx.SaveChangesAsync();
    }

    // --- Codes --------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("ABCD-EF12", "ABCDEF12")]
    [DataRow("abcd ef12", "ABCDEF12")]
    [DataRow("0O1I-L234", "00111234")]
    [DataRow("ABCDEF1", null)]
    [DataRow("ABCDEF123", null)]
    [DataRow("ABCD-EFU2", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void NormaliseCode_AcceptsWhatPeopleType_AndNothingElse(string? typed, string? expected)
    {
        Assert.AreEqual(expected, KioskEnrolmentService.NormaliseCode(typed));
    }

    [TestMethod]
    public async Task Start_ShowsACode_AndStoresOnlyHashes()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();

        StringAssert.Matches(started.PairingCode, new System.Text.RegularExpressions.Regex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$"));
        Assert.AreEqual(_clock.UtcNow.Add(KioskEnrolmentService.CodeLifetime), started.ExpiresUtc);

        await using ApplicationDbContext ctx = new(_options);
        ClientEnrolmentRequest row = await ctx.ClientEnrolmentRequests.SingleAsync();

        Assert.AreNotEqual(started.PairingCode.Replace("-", ""), row.PairingCodeHash);
        Assert.AreNotEqual(started.PollToken, row.PollTokenHash);
        Assert.AreEqual("BAR-PC", row.MachineName);
        Assert.AreEqual("10.0.0.9", row.RequestIp);
    }

    [TestMethod]
    public async Task Start_Again_RetiresTheKiosksOldCode()
    {
        Guid clientId = Guid.NewGuid();
        KioskEnrolmentStartedDTO first = await StartAsync(clientId);
        await StartAsync(clientId);

        Result<PendingKioskEnrolment> found = await GetService().FindPendingAsync(first.PairingCode);

        Assert.IsFalse(found.IsSuccess);
    }

    // --- Polling ------------------------------------------------------------------------------

    [TestMethod]
    public async Task Poll_WithTheWrongToken_LooksLikeNoSuchRequest()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();

        Assert.IsFalse((await GetService().PollAsync(started.RequestId, "wrong")).IsSuccess);
        Assert.IsFalse((await GetService().PollAsync(started.RequestId, null)).IsSuccess);
        Assert.IsFalse((await GetService().PollAsync(Guid.NewGuid(), started.PollToken)).IsSuccess);
    }

    [TestMethod]
    public async Task Poll_IsPending_UntilTheCodeRunsOut()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();

        Assert.AreEqual(KioskEnrolmentState.Pending, (await GetService().PollAsync(started.RequestId, started.PollToken)).Data!.State);

        _clock.Advance(KioskEnrolmentService.CodeLifetime);

        Assert.AreEqual(KioskEnrolmentState.Expired, (await GetService().PollAsync(started.RequestId, started.PollToken)).Data!.State);
    }

    [TestMethod]
    public async Task Poll_AfterApproval_ReturnsAWorkingCredential_ExactlyOnce()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();
        Assert.IsTrue((await GetService().ApproveAsync(started.PairingCode, "Bar PC", null)).IsSuccess);

        Result<KioskEnrolmentStatusDTO> first = await GetService().PollAsync(started.RequestId, started.PollToken);
        Result<KioskEnrolmentStatusDTO> second = await GetService().PollAsync(started.RequestId, started.PollToken);

        Assert.AreEqual(KioskEnrolmentState.Approved, first.Data!.State);
        Assert.IsNotNull(first.Data.Credential);
        Assert.AreEqual(KioskEnrolmentState.Expired, second.Data!.State);
        Assert.IsNull(second.Data.Credential, "The credential was handed out twice.");

        KioskCredentialService credentials = new(Factory(), _cache, _clock, NullLogger<KioskCredentialService>.Instance);
        Result<KioskIdentity> identity = await credentials.ValidateAsync(first.Data.Credential);

        Assert.IsTrue(identity.IsSuccess, identity.Error);
        Assert.AreEqual(CompanyA, identity.Data!.CompanyId);
    }

    [TestMethod]
    public async Task Poll_AfterRejection_SaysSo()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();
        Assert.IsTrue((await GetService().RejectAsync(started.PairingCode)).IsSuccess);

        Result<KioskEnrolmentStatusDTO> polled = await GetService().PollAsync(started.RequestId, started.PollToken);

        Assert.AreEqual(KioskEnrolmentState.Rejected, polled.Data!.State);
        Assert.IsNull(polled.Data.Credential);
    }

    // --- Approving ----------------------------------------------------------------------------

    [TestMethod]
    public async Task Approve_CreatesTheKioskInTheAdminsCompany_NotTheSeedCompany()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();

        Result<Guid> approved = await GetService().ApproveAsync(started.PairingCode.ToLowerInvariant(), "  Bar PC  ", CompanyALocation);

        Assert.IsTrue(approved.IsSuccess, approved.Error);

        await using ApplicationDbContext ctx = new(_options);
        Client client = await ctx.Clients.SingleAsync(x => x.Id == approved.Data);

        Assert.AreEqual(CompanyA, client.CompanyId);
        Assert.AreEqual("Bar PC", client.Name);
        Assert.AreEqual("1.0.43", client.ReportedClientVersion);
        _clientService.Verify(x => x.SetClientLocationAsync(approved.Data, CompanyALocation), Times.Once);
    }

    [TestMethod]
    public async Task Approve_ReusesAnExistingKioskOfTheSameCompany_KeepingItsSettings()
    {
        Guid clientId = Guid.NewGuid();
        await AddClientAsync(clientId, CompanyA);
        KioskEnrolmentStartedDTO started = await StartAsync(clientId);

        Result<PendingKioskEnrolment> found = await GetService().FindPendingAsync(started.PairingCode);
        Assert.AreEqual("Existing kiosk", found.Data!.ExistingName);

        Result<Guid> approved = await GetService().ApproveAsync(started.PairingCode, "Renamed", null);

        Assert.IsTrue(approved.IsSuccess, approved.Error);

        await using ApplicationDbContext ctx = new(_options);
        Client client = await ctx.Clients.SingleAsync(x => x.Id == clientId);
        Assert.AreEqual("Renamed", client.Name);
        Assert.AreEqual(900, client.MusicCacheLimitMb);
    }

    [TestMethod]
    public async Task Approve_RefusesAKioskThatBelongsToAnotherCompany()
    {
        Guid clientId = Guid.NewGuid();
        await AddClientAsync(clientId, CompanyB);
        KioskEnrolmentStartedDTO started = await StartAsync(clientId);

        Assert.IsFalse((await GetService(CompanyA).FindPendingAsync(started.PairingCode)).IsSuccess);
        Assert.IsFalse((await GetService(CompanyA).ApproveAsync(started.PairingCode, "Mine now", null)).IsSuccess);

        await using ApplicationDbContext ctx = new(_options);
        Assert.AreEqual(CompanyB, (await ctx.Clients.SingleAsync(x => x.Id == clientId)).CompanyId);
    }

    [TestMethod]
    public async Task Approve_RefusesAnotherCompanysLocation()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();

        Result<Guid> approved = await GetService(CompanyA).ApproveAsync(started.PairingCode, "Bar PC", CompanyBLocation);

        Assert.IsFalse(approved.IsSuccess);

        await using ApplicationDbContext ctx = new(_options);
        Assert.IsFalse(await ctx.Clients.AnyAsync());
    }

    [TestMethod]
    public async Task Approve_IsAdminOnly()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();

        Assert.IsFalse((await GetService(role: "Manager").ApproveAsync(started.PairingCode, "Bar PC", null)).IsSuccess);
        Assert.IsFalse((await GetService(role: null).FindPendingAsync(started.PairingCode)).IsSuccess);
        Assert.IsFalse((await GetService(role: "Manager").RejectAsync(started.PairingCode)).IsSuccess);
    }

    [TestMethod]
    public async Task Approve_NeedsACompany()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();

        Assert.IsFalse((await GetService(companyId: null).ApproveAsync(started.PairingCode, "Bar PC", null)).IsSuccess);
    }

    [TestMethod]
    public async Task Approve_RefusesAnExpiredCode()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();

        _clock.Advance(KioskEnrolmentService.CodeLifetime);

        Assert.IsFalse((await GetService().ApproveAsync(started.PairingCode, "Bar PC", null)).IsSuccess);
    }

    [TestMethod]
    public async Task Approve_RefusesABlankName()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();

        Assert.IsFalse((await GetService().ApproveAsync(started.PairingCode, "   ", null)).IsSuccess);
    }

    [TestMethod]
    public async Task Approve_ACodeOnlyWorksOnce()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();

        Assert.IsTrue((await GetService().ApproveAsync(started.PairingCode, "Bar PC", null)).IsSuccess);
        Assert.IsFalse((await GetService().ApproveAsync(started.PairingCode, "Bar PC", null)).IsSuccess);
        Assert.IsFalse((await GetService().RejectAsync(started.PairingCode)).IsSuccess);
    }

    [TestMethod]
    public async Task WrongCodes_LockTheAdminOut_ForAWhile()
    {
        KioskEnrolmentStartedDTO started = await StartAsync();

        for (int i = 0; i < 10; i++)
        {
            Assert.IsFalse((await GetService().FindPendingAsync("ZZZZ-ZZZZ")).IsSuccess);
        }

        Result<PendingKioskEnrolment> withRightCode = await GetService().FindPendingAsync(started.PairingCode);

        Assert.IsFalse(withRightCode.IsSuccess);
        StringAssert.Contains(withRightCode.Error, "Too many");

        // Another Admin isn't affected.
        Assert.IsTrue((await GetService(userId: "admin-a2").FindPendingAsync(started.PairingCode)).IsSuccess);
    }





}
