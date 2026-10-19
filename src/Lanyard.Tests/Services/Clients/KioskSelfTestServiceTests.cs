using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.Kiosk;
using Lanyard.Shared.SignalR;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Clients;

[TestClass]
public class KioskSelfTestServiceTests
{
    private const string ConnectionId = "connection-1";
    private const string AdminId = "admin-1";
    private const int CompanyId = 7;

    internal sealed class TestTenant(int? companyId, bool isSystem = false) : ITenantContext
    {
        public bool IsSystem => isSystem;
        public int? CompanyId => isSystem ? null : companyId;
        public bool IsPlatformAdmin => false;
        public int? ManageableCompanyId => isSystem ? null : companyId ?? -1;
        public bool CanManageCompany(int id) => ManageableCompanyId is null || ManageableCompanyId == id;
    }

    private sealed class Harness
    {
        public required KioskSelfTestService Service { get; init; }
        public required Mock<ISingleClientProxy> Proxy { get; init; }
        public required Mock<IClientService> ClientService { get; init; }
        public required TestClock Clock { get; init; }
        public required DbContextOptions<ApplicationDbContext> DbOptions { get; init; }
        public Guid ClientId { get; init; }

        // The request the kiosk was last sent.
        public KioskSelfTestRequestDTO? Sent { get; set; }
    }

    private static KioskSelfTestResultDTO Reply(Guid runId, params KioskSelfTestCheckDTO[] checks)
    {
        return new KioskSelfTestResultDTO
        {
            RunId = runId,
            ClientVersion = "1.0.42",
            StartedUtc = DateTime.UtcNow,
            FinishedUtc = DateTime.UtcNow,
            Checks = checks.Length > 0
                ? [.. checks]
                :
                [
                    SelfTestRules.Check(SelfTestCheckKeys.WebView2Runtime, SelfTestOutcome.Pass, null, "Version 141.0.0.0."),
                    SelfTestRules.Check(SelfTestCheckKeys.AudioDevice, SelfTestOutcome.Pass, null, "\"Speakers\" is connected."),
                    SelfTestRules.Check(SelfTestCheckKeys.CaptureDriver, SelfTestOutcome.Fail, SelfTestCodes.NpcapMissing, "Npcap isn't installed."),
                    SelfTestRules.Check(SelfTestCheckKeys.AudioTone, SelfTestOutcome.Skipped, SelfTestCodes.MusicPlaying, "Not played because music is playing."),
                ]
        };
    }

    private static Harness Build(
        bool connected = true,
        bool isAdmin = true,
        ITenantContext? tenant = null,
        Func<KioskSelfTestRequestDTO, KioskSelfTestResultDTO?>? kiosk = null,
        Exception? kioskThrows = null)
    {
        Guid clientId = Guid.NewGuid();

        Mock<IClientService> clientService = new();
        clientService.Setup(c => c.GetClientFromIdAsync(clientId))
            .ReturnsAsync(Result<Client?>.Ok(new Client { Id = clientId, CompanyId = CompanyId, Name = "Kiosk", MostRecentConnectionId = ConnectionId }));
        clientService.Setup(c => c.GetClientFromIdAsync(It.Is<Guid>(id => id != clientId)))
            .ReturnsAsync(Result<Client?>.Ok(null));
        clientService.Setup(c => c.IsClientConnectedAsync(clientId))
            .ReturnsAsync(Result<bool>.Ok(connected));

        Harness? harness = null;

        Mock<ISingleClientProxy> proxy = new();
        var invoke = proxy.Setup(p => p.InvokeCoreAsync<KioskSelfTestResultDTO>(ClientHubMethods.RunSelfTest, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()));

        if (kioskThrows != null)
        {
            invoke.ThrowsAsync(kioskThrows);
        }
        else
        {
            invoke.ReturnsAsync((string _, object?[] args, CancellationToken _) =>
            {
                KioskSelfTestRequestDTO request = (KioskSelfTestRequestDTO)args[0]!;
                harness!.Sent = request;
                return (kiosk ?? (r => Reply(r.RunId)))(request)!;
            });
        }

        Mock<IHubClients> hubClients = new();
        hubClients.Setup(c => c.Client(ConnectionId)).Returns(proxy.Object);

        Mock<IHubContext<SignalRControlHub>> hubContext = new();
        hubContext.Setup(h => h.Clients).Returns(hubClients.Object);

        Mock<ISecurityService> security = new();
        security.Setup(s => s.IsCurrentUserInRoleAsync("Admin")).ReturnsAsync(isAdmin);
        security.Setup(s => s.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok(AdminId));

        TestClock clock = new(new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc));

        DbContextOptions<ApplicationDbContext> dbOptions = SchedulingTestHelpers.GetInMemoryOptions();
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(dbOptions));

        KioskSelfTestService service = new(
            clientService.Object,
            hubContext.Object,
            new ClientDiagnosticsStore(clock),
            factory.Object,
            tenant ?? new TestTenant(CompanyId),
            security.Object,
            clock,
            NullLogger<KioskSelfTestService>.Instance);

        harness = new Harness
        {
            Service = service,
            Proxy = proxy,
            ClientService = clientService,
            Clock = clock,
            DbOptions = dbOptions,
            ClientId = clientId
        };

        return harness;
    }

    private static async Task<List<ClientSelfTestRun>> StoredRunsAsync(Harness h)
    {
        await using ApplicationDbContext ctx = new(h.DbOptions);
        return await ctx.ClientSelfTestRuns.Include(x => x.Checks).ToListAsync();
    }

    private static void AssertKioskNeverAsked(Harness h)
    {
        h.Proxy.Verify(p => p.InvokeCoreAsync<KioskSelfTestResultDTO>(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RunAsync_StoresTheChecklist_GroupedWithTitlesAndHints()
    {
        Harness h = Build();

        Result<KioskSelfTestRunView> result = await h.Service.RunAsync(h.ClientId, flashDmx: true);

        Assert.IsTrue(result.IsSuccess, result.Error);
        KioskSelfTestRunView run = result.Data!;

        Assert.AreEqual(ClientSelfTestStatus.Completed, run.Status);
        Assert.AreEqual("1.0.42", run.ClientVersion);
        Assert.IsTrue(run.FlashRequested);
        Assert.IsTrue(h.Sent!.FlashDmx);
        Assert.AreEqual(run.Id, h.Sent.RunId);

        // Groups come out in the fixed order, whatever order the kiosk reported them in.
        CollectionAssert.AreEqual(new[] { "Audio", "Laser tag network", "Projection display" }, run.Groups.Select(g => g.Title).ToArray());
        CollectionAssert.AreEqual(new[] { SelfTestCheckKeys.AudioDevice, SelfTestCheckKeys.AudioTone }, run.Groups[0].Checks.Select(c => c.Key).ToArray());

        KioskSelfTestCheckView npcap = run.Groups[1].Checks.Single();
        Assert.AreEqual("Npcap installed", npcap.Title);
        StringAssert.Contains(npcap.Hint, "npcap.com");
        Assert.IsNull(run.Groups[0].Checks[0].Hint, "A pass needs no hint");

        Assert.AreEqual(1, run.Count(SelfTestOutcome.Fail));
        Assert.AreEqual(2, run.Count(SelfTestOutcome.Pass));

        List<ClientSelfTestRun> stored = await StoredRunsAsync(h);
        Assert.HasCount(1, stored);
        Assert.AreEqual(CompanyId, stored[0].CompanyId);
        Assert.AreEqual(AdminId, stored[0].RequestedByUserId);
        Assert.HasCount(4, stored[0].Checks);
    }

    [TestMethod]
    public async Task RunAsync_KioskTimesOut_StoresTheRunAsTimedOut()
    {
        Harness h = Build(kioskThrows: new OperationCanceledException());

        Result<KioskSelfTestRunView> result = await h.Service.RunAsync(h.ClientId, false);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(ClientSelfTestStatus.TimedOut, result.Data!.Status);
        StringAssert.Contains(result.Data.Error, "didn't reply");
        Assert.AreEqual(ClientSelfTestStatus.TimedOut, (await StoredRunsAsync(h)).Single().Status);
    }

    [TestMethod]
    public async Task RunAsync_KioskTooOld_SaysHowToUpdate()
    {
        Harness h = Build(kioskThrows: new HubException("Client didn't provide a result."));

        Result<KioskSelfTestRunView> result = await h.Service.RunAsync(h.ClientId, false);

        Assert.AreEqual(ClientSelfTestStatus.KioskTooOld, result.Data!.Status);
        StringAssert.Contains(result.Data.Error, "older Lanyard Client");
    }

    [TestMethod]
    public async Task RunAsync_RejectsAReplyForAnotherRun()
    {
        Harness h = Build(kiosk: r => Reply(Guid.NewGuid()));

        Result<KioskSelfTestRunView> result = await h.Service.RunAsync(h.ClientId, false);

        Assert.AreEqual(ClientSelfTestStatus.Failed, result.Data!.Status);
        StringAssert.Contains(result.Data.Error, "different self-test");
        Assert.HasCount(0, (await StoredRunsAsync(h)).Single().Checks);
    }

    [TestMethod]
    public async Task RunAsync_RejectsOversizeAndEmptyReplies()
    {
        KioskSelfTestCheckDTO[] tooMany = Enumerable.Range(0, SelfTestRules.MaxChecks + 1)
            .Select(_ => SelfTestRules.Check(SelfTestCheckKeys.AudioDevice, SelfTestOutcome.Pass, null, null))
            .ToArray();

        Harness oversize = Build(kiosk: r => Reply(r.RunId, tooMany));
        StringAssert.Contains((await oversize.Service.RunAsync(oversize.ClientId, false)).Data!.Error, "more checks");

        Harness empty = Build(kiosk: _ => null);
        StringAssert.Contains((await empty.Service.RunAsync(empty.ClientId, false)).Data!.Error, "empty reply");
    }

    [TestMethod]
    public async Task RunAsync_CapsWhatTheKioskSends_AndKeepsUnknownChecks()
    {
        Harness h = Build(kiosk: r => Reply(r.RunId,
            new KioskSelfTestCheckDTO { Key = "printer.paper", Outcome = (SelfTestOutcome)99, Code = new string('c', 80), Detail = new string('d', 2000) }));

        KioskSelfTestRunView run = (await h.Service.RunAsync(h.ClientId, false)).Data!;
        KioskSelfTestCheckView check = run.Groups.Single().Checks.Single();

        Assert.AreEqual("Other checks", run.Groups[0].Title);
        StringAssert.StartsWith(check.Title, "Unrecognised check");
        Assert.AreEqual(SelfTestOutcome.Fail, check.Outcome, "An outcome the server doesn't know is shown as a failure");
        Assert.AreEqual(50, check.Code!.Length);
        Assert.AreEqual(SelfTestRules.MaxDetailLength, check.Detail!.Length);
    }

    [TestMethod]
    public async Task RunAsync_RefusesASystemCaller_WithoutAskingTheKiosk()
    {
        // No signed-in user means the unfiltered system tenant, which would reach every company.
        Harness h = Build(tenant: new TestTenant(null, isSystem: true));

        Result<KioskSelfTestRunView> result = await h.Service.RunAsync(h.ClientId, false);

        Assert.IsFalse(result.IsSuccess);
        AssertKioskNeverAsked(h);
        h.ClientService.Verify(c => c.GetClientFromIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [TestMethod]
    public async Task RunAsync_RefusesASignedInUserWithNoCompany()
    {
        Harness h = Build(tenant: new TestTenant(null));

        Assert.IsFalse((await h.Service.RunAsync(h.ClientId, false)).IsSuccess);
        AssertKioskNeverAsked(h);
    }

    [TestMethod]
    public async Task RunAsync_RefusesANonAdmin()
    {
        Harness h = Build(isAdmin: false);

        Result<KioskSelfTestRunView> result = await h.Service.RunAsync(h.ClientId, false);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Admin");
        AssertKioskNeverAsked(h);
    }

    [TestMethod]
    public async Task RunAsync_AnotherCompanysKiosk_IsNotFound()
    {
        // The tenant-filtered lookup returns nothing for a kiosk in another company.
        Harness h = Build();

        Result<KioskSelfTestRunView> result = await h.Service.RunAsync(Guid.NewGuid(), false);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Couldn't find this kiosk.", result.Error);
        AssertKioskNeverAsked(h);
        Assert.HasCount(0, await StoredRunsAsync(h));
    }

    [TestMethod]
    public async Task RunAsync_OfflineKiosk_FailsWithoutStoringAnything()
    {
        Harness h = Build(connected: false);

        Result<KioskSelfTestRunView> result = await h.Service.RunAsync(h.ClientId, false);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "isn't connected");
        AssertKioskNeverAsked(h);
        Assert.HasCount(0, await StoredRunsAsync(h));
    }

    [TestMethod]
    public async Task RunAsync_CoolsDownBetweenRuns()
    {
        Harness h = Build();

        Assert.IsTrue((await h.Service.RunAsync(h.ClientId, false)).IsSuccess);

        Result<KioskSelfTestRunView> tooSoon = await h.Service.RunAsync(h.ClientId, false);
        Assert.IsFalse(tooSoon.IsSuccess);
        StringAssert.Contains(tooSoon.Error, "a moment ago");

        h.Clock.Advance(ClientDiagnosticsStore.SelfTestCooldown);
        Assert.IsTrue((await h.Service.RunAsync(h.ClientId, false)).IsSuccess);
    }

    [TestMethod]
    public async Task RunAsync_OneAtATimePerKiosk()
    {
        TaskCompletionSource<KioskSelfTestResultDTO> release = new();
        Harness h = Build();
        h.Proxy.Setup(p => p.InvokeCoreAsync<KioskSelfTestResultDTO>(ClientHubMethods.RunSelfTest, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns((string _, object?[] args, CancellationToken _) => release.Task.ContinueWith(t => Reply(((KioskSelfTestRequestDTO)args[0]!).RunId)));

        Task<Result<KioskSelfTestRunView>> first = h.Service.RunAsync(h.ClientId, false);

        Result<KioskSelfTestRunView> second = await h.Service.RunAsync(h.ClientId, false);
        Assert.IsFalse(second.IsSuccess);
        StringAssert.Contains(second.Error, "already running");

        release.SetResult(new KioskSelfTestResultDTO());
        Assert.IsTrue((await first).IsSuccess);
    }

    [TestMethod]
    public async Task GetHistoryAsync_NewestFirst_WithTheRequestersName()
    {
        Harness h = Build();

        await using (ApplicationDbContext ctx = new(h.DbOptions))
        {
            ctx.Users.Add(new UserProfile { Id = AdminId, UserName = "ada", FirstName = "Ada", LastName = "Min" });
            await ctx.SaveChangesAsync();
        }

        await h.Service.RunAsync(h.ClientId, false);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.Service.RunAsync(h.ClientId, true);

        Result<List<KioskSelfTestRunView>> history = await h.Service.GetHistoryAsync(h.ClientId);

        Assert.IsTrue(history.IsSuccess, history.Error);
        List<KioskSelfTestRunView> runs = history.Data!;
        Assert.HasCount(2, runs);
        Assert.IsTrue(runs[0].FlashRequested, "Newest first");
        Assert.AreEqual("Ada Min", runs[0].RequestedByName);

        Assert.HasCount(1, (await h.Service.GetHistoryAsync(h.ClientId, take: 1)).Data!);
    }

    [TestMethod]
    public async Task GetHistoryAsync_RefusesSystemCallersAndNonAdmins()
    {
        Assert.IsFalse((await Build(tenant: new TestTenant(null, isSystem: true)).Service.GetHistoryAsync(Guid.NewGuid())).IsSuccess);
        Assert.IsFalse((await Build(isAdmin: false).Service.GetHistoryAsync(Guid.NewGuid())).IsSuccess);
    }

    [TestMethod]
    public async Task TenantFilter_HidesAnotherCompanysRunsAndChecks()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        Guid clientId = Guid.NewGuid();

        await using (ApplicationDbContext seed = new(options))
        {
            seed.ClientSelfTestRuns.Add(new ClientSelfTestRun
            {
                Id = Guid.NewGuid(),
                CompanyId = 1,
                ClientId = clientId,
                RequestedAtUtc = DateTime.UtcNow,
                Status = ClientSelfTestStatus.Completed,
                Checks = [new ClientSelfTestCheck { Id = Guid.NewGuid(), Key = SelfTestCheckKeys.AudioDevice, Outcome = SelfTestOutcome.Pass }]
            });
            await seed.SaveChangesAsync();
        }

        await using ApplicationDbContext otherCompany = new(options, new SignedInTenant(2));
        await using ApplicationDbContext sameCompany = new(options, new SignedInTenant(1));

        Assert.AreEqual(0, await otherCompany.ClientSelfTestRuns.CountAsync());
        Assert.AreEqual(0, await otherCompany.ClientSelfTestChecks.CountAsync(), "Checks are filtered through their run, not only via the run");
        Assert.AreEqual(1, await sameCompany.ClientSelfTestRuns.CountAsync());
        Assert.AreEqual(1, await sameCompany.ClientSelfTestChecks.CountAsync());
    }

    private sealed class SignedInTenant(int? companyId) : ITenantProvider
    {
        public bool IsSystem => false;
        public int? CompanyId => companyId;
    }
}
