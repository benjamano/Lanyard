using Lanyard.Application.Services.AudioClips;
using Lanyard.Application.SignalR;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.AudioClips;
using Lanyard.Shared.DTO;
using Lanyard.Shared.Enum;
using Lanyard.Shared.SignalR;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.AudioClips;

[TestClass]
public class AudioClipPlaybackServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 21, 45, 0, DateTimeKind.Utc);

    // SignalRControlHub's connection set is process-static; the service exposes a seam instead.
    private sealed class TestService(ISystemDbContextFactory factory, IHubContext<SignalRControlHub> hub, TimeProvider clock)
        : AudioClipPlaybackService(factory, hub, clock, NullLogger<AudioClipPlaybackService>.Instance)
    {
        public HashSet<string> Connected { get; } = [];

        protected override bool IsConnected(string? connectionId) => connectionId is not null && Connected.Contains(connectionId);
    }

    private sealed class Setup
    {
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required TestService Service { get; init; }
        public required Company Company { get; init; }
        public required Location Ipswich { get; init; }
        public required Location Norwich { get; init; }
        public required Guid ClipId { get; init; }
        public required Dictionary<string, List<(string Method, object?[] Args)>> Sent { get; init; }
        public required Mock<IHubClients> HubClients { get; init; }
    }

    private static async Task<Setup> SeedAsync(int? clipLocation = null)
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Location norwich;
        Guid clipId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            norwich = new Location { CompanyId = company.Id, Name = "Norwich", IsActive = true };
            ctx.Locations.Add(norwich);
            await ctx.SaveChangesAsync();

            FileMetadata file = new() { Id = Guid.NewGuid(), CompanyId = company.Id, FileName = "closing.mp3", FilePath = "closing.mp3", IsActive = true };
            ctx.FileMetadata.Add(file);
            ctx.AudioClips.Add(new AudioClip
            {
                Id = clipId,
                CompanyId = company.Id,
                Name = "Closing time",
                FileMetadataId = file.Id,
                DurationSeconds = 12,
                Volume = 90,
                LocationId = clipLocation == 1 ? ipswich.Id : clipLocation == 2 ? norwich.Id : null,
                CreateDate = Now
            });
            await ctx.SaveChangesAsync();
        }

        Dictionary<string, List<(string, object?[])>> sent = [];
        Mock<IHubClients> hubClients = new();
        hubClients.Setup(c => c.Client(It.IsAny<string>())).Returns<string>(connectionId =>
        {
            Mock<ISingleClientProxy> proxy = new();
            proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Callback<string, object?[], CancellationToken>((method, args, _) =>
                {
                    if (!sent.TryGetValue(connectionId, out List<(string, object?[])>? calls))
                    {
                        sent[connectionId] = calls = [];
                    }

                    calls.Add((method, args));
                })
                .Returns(Task.CompletedTask);

            return proxy.Object;
        });

        Mock<IHubContext<SignalRControlHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(hubClients.Object);

        TestService service = new(SchedulingTestHelpers.GetFactory(options), hub.Object, new TestClock(Now));

        return new Setup
        {
            Options = options,
            Service = service,
            Company = company,
            Ipswich = ipswich,
            Norwich = norwich,
            ClipId = clipId,
            Sent = sent,
            HubClients = hubClients
        };
    }

    private static async Task<Guid> AddKioskAsync(Setup s, string name, Location location, string? version = AudioClipFormats.MinimumClientVersion, bool connected = true, int? companyId = null)
    {
        Guid id = Guid.NewGuid();
        string connectionId = $"conn-{name}";

        await using ApplicationDbContext ctx = new(s.Options);
        ctx.Clients.Add(new Client
        {
            Id = id,
            CompanyId = companyId ?? s.Company.Id,
            Name = name,
            LocationId = location.Id,
            MostRecentConnectionId = connectionId,
            ReportedClientVersion = version
        });
        await ctx.SaveChangesAsync();

        if (connected)
        {
            s.Service.Connected.Add(connectionId);
        }

        return id;
    }

    private static AudioClipPlayRequest Request(Setup s, params Guid[] kiosks) =>
        new(s.ClipId, kiosks, s.Company.Id, AudioClipPlaySource.Manual, "user-1");

    [TestMethod]
    public async Task Play_SendsTheClipToTheKiosksConnection()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(Request(s, lobby));

        Assert.IsTrue(result.IsSuccess, result.Error);
        AudioClipKioskOutcome outcome = result.Data!.Single();
        Assert.AreEqual(AudioClipPlayStatus.Sent, outcome.Status);

        (string method, object?[] args) = s.Sent["conn-Lobby"].Single();
        Assert.AreEqual(ClientHubMethods.PlayAudioClip, method);

        AudioClipPlayCommand command = (AudioClipPlayCommand)args.Single()!;
        Assert.AreEqual(outcome.PlayId, command.PlayId);
        Assert.AreEqual(s.ClipId, command.ClipId);
        Assert.AreEqual(".mp3", command.FileExtension);
        Assert.AreEqual(90, command.Volume);
        Assert.AreEqual(12, command.DurationSeconds);
    }

    [TestMethod]
    public async Task Play_UsesTheCompanyDuckDefaults_WhenTheClipHasNone()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        await s.Service.PlayAsync(Request(s, lobby));

        AudioClipPlayCommand command = (AudioClipPlayCommand)s.Sent["conn-Lobby"].Single().Args.Single()!;
        Assert.AreEqual(AudioClipDefaults.DuckToPercent, command.DuckToPercent);
        Assert.AreEqual(AudioClipDefaults.FadeDownMs, command.FadeDownMs);
        Assert.AreEqual(AudioClipDefaults.FadeUpMs, command.FadeUpMs);
    }

    [TestMethod]
    public async Task Play_UsesTheClipsOwnDuck_WhenSet()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            AudioClip clip = await ctx.AudioClips.SingleAsync();
            clip.DuckToPercent = 0;
            clip.FadeDownMs = 200;
            clip.FadeUpMs = 3000;
            await ctx.SaveChangesAsync();
        }

        await s.Service.PlayAsync(Request(s, lobby));

        AudioClipPlayCommand command = (AudioClipPlayCommand)s.Sent["conn-Lobby"].Single().Args.Single()!;
        Assert.AreEqual(0, command.DuckToPercent);
        Assert.AreEqual(200, command.FadeDownMs);
        Assert.AreEqual(3000, command.FadeUpMs);
    }

    [TestMethod]
    public async Task Play_RecordsEachKioskAndHowItWent()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);
        Guid bar = await AddKioskAsync(s, "Bar", s.Ipswich, connected: false);
        Guid old = await AddKioskAsync(s, "Old", s.Ipswich, version: "1.0.41");
        Guid unreported = await AddKioskAsync(s, "Unreported", s.Ipswich, version: null);

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(Request(s, lobby, bar, old, unreported));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(AudioClipPlayStatus.Sent, result.Data!.Single(x => x.ClientId == lobby).Status);
        Assert.AreEqual(AudioClipPlayStatus.Offline, result.Data!.Single(x => x.ClientId == bar).Status);
        Assert.AreEqual(AudioClipPlayStatus.OutdatedKiosk, result.Data!.Single(x => x.ClientId == old).Status);
        Assert.AreEqual(AudioClipPlayStatus.OutdatedKiosk, result.Data!.Single(x => x.ClientId == unreported).Status);
        CollectionAssert.AreEquivalent(new[] { "conn-Lobby" }, s.Sent.Keys.ToArray());

        await using ApplicationDbContext ctx = new(s.Options);
        List<AudioClipPlay> plays = await ctx.AudioClipPlays.ToListAsync();
        Assert.AreEqual(4, plays.Count);
        Assert.IsTrue(plays.All(x => x.Source == AudioClipPlaySource.Manual && x.TriggeredByUserId == "user-1"));
        Assert.IsNull(plays.Single(x => x.ClientId == lobby).FinishedAtUtc);
        Assert.IsNotNull(plays.Single(x => x.ClientId == bar).FinishedAtUtc);
    }

    [TestMethod]
    public async Task Play_RefusesAClipLimitedToAnotherLocation()
    {
        Setup s = await SeedAsync(clipLocation: 2);
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(Request(s, lobby));

        AudioClipKioskOutcome outcome = result.Data!.Single();
        Assert.AreEqual(AudioClipPlayStatus.Failed, outcome.Status);
        StringAssert.Contains(outcome.Error, "Norwich");
        Assert.AreEqual(0, s.Sent.Count);
    }

    [TestMethod]
    public async Task Play_PlaysAClipLimitedToTheKiosksOwnLocation()
    {
        Setup s = await SeedAsync(clipLocation: 1);
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(Request(s, lobby));

        Assert.AreEqual(AudioClipPlayStatus.Sent, result.Data!.Single().Status);
    }

    [TestMethod]
    public async Task Play_IgnoresAnotherCompanysKiosk()
    {
        Setup s = await SeedAsync();
        Company other;
        await using (ApplicationDbContext ctx = new(s.Options))
        {
            other = new Company { Name = "Rival", IsActive = true };
            ctx.Companies.Add(other);
            await ctx.SaveChangesAsync();
        }

        Guid theirs = await AddKioskAsync(s, "Theirs", s.Ipswich, companyId: other.Id);

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(Request(s, theirs));

        Assert.AreEqual(AudioClipPlayStatus.Failed, result.Data!.Single().Status);
        Assert.IsNull(result.Data!.Single().PlayId);
        Assert.AreEqual(0, s.Sent.Count);
    }

    [TestMethod]
    public async Task Play_FailsForAnotherCompanysClip()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(new AudioClipPlayRequest(s.ClipId, [lobby], s.Company.Id + 1, AudioClipPlaySource.Manual, null));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(0, s.Sent.Count);
    }

    [TestMethod]
    public async Task Play_FailsForADeletedClip()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            (await ctx.AudioClips.SingleAsync()).DeleteDate = Now;
            await ctx.SaveChangesAsync();
        }

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(Request(s, lobby));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Play_FailsWhenNoKioskIsChosen()
    {
        Setup s = await SeedAsync();

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(Request(s));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Play_ANewClipReplacesWhatTheKioskIsPlaying()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        Guid first = (await s.Service.PlayAsync(Request(s, lobby))).Data!.Single().PlayId!.Value;
        Guid second = (await s.Service.PlayAsync(Request(s, lobby))).Data!.Single().PlayId!.Value;

        Assert.AreNotEqual(first, second);
        Assert.AreEqual(second, s.Service.GetNowPlaying(lobby)!.PlayId);
    }

    [TestMethod]
    public async Task Report_UpdatesThePlayAndClearsNowPlayingWhenItEnds()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);
        Guid playId = (await s.Service.PlayAsync(Request(s, lobby))).Data!.Single().PlayId!.Value;
        List<Guid> changed = [];
        s.Service.OnClipPlaybackChanged += changed.Add;

        await s.Service.ReportFromClientAsync(lobby, new AudioClipPlaybackReport { PlayId = playId, Status = AudioClipPlaybackStatus.Playing });
        Assert.AreEqual(AudioClipPlayStatus.Playing, s.Service.GetNowPlaying(lobby)!.Status);

        await s.Service.ReportFromClientAsync(lobby, new AudioClipPlaybackReport { PlayId = playId, Status = AudioClipPlaybackStatus.Finished });

        Assert.IsNull(s.Service.GetNowPlaying(lobby));
        CollectionAssert.AreEqual(new[] { lobby, lobby }, changed);

        await using ApplicationDbContext ctx = new(s.Options);
        AudioClipPlay play = await ctx.AudioClipPlays.SingleAsync();
        Assert.AreEqual(AudioClipPlayStatus.Finished, play.Status);
        Assert.AreEqual(Now, play.FinishedAtUtc);
    }

    [TestMethod]
    public async Task Report_FromAKioskThePlayWasNotSentTo_IsIgnored()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);
        Guid bar = await AddKioskAsync(s, "Bar", s.Ipswich);
        Guid playId = (await s.Service.PlayAsync(Request(s, lobby))).Data!.Single().PlayId!.Value;

        Result<bool> result = await s.Service.ReportFromClientAsync(bar, new AudioClipPlaybackReport { PlayId = playId, Status = AudioClipPlaybackStatus.Failed, Error = "spoofed" });

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Data);

        await using ApplicationDbContext ctx = new(s.Options);
        Assert.AreEqual(AudioClipPlayStatus.Sent, (await ctx.AudioClipPlays.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task Report_AfterThePlayEnded_DoesNotReopenIt()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);
        Guid playId = (await s.Service.PlayAsync(Request(s, lobby))).Data!.Single().PlayId!.Value;

        await s.Service.ReportFromClientAsync(lobby, new AudioClipPlaybackReport { PlayId = playId, Status = AudioClipPlaybackStatus.Stopped });
        Result<bool> late = await s.Service.ReportFromClientAsync(lobby, new AudioClipPlaybackReport { PlayId = playId, Status = AudioClipPlaybackStatus.Playing });

        Assert.IsFalse(late.Data);

        await using ApplicationDbContext ctx = new(s.Options);
        Assert.AreEqual(AudioClipPlayStatus.Stopped, (await ctx.AudioClipPlays.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task Report_ForACutOffClip_KeepsTheNewClipAsNowPlaying()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);
        Guid first = (await s.Service.PlayAsync(Request(s, lobby))).Data!.Single().PlayId!.Value;
        Guid second = (await s.Service.PlayAsync(Request(s, lobby))).Data!.Single().PlayId!.Value;

        await s.Service.ReportFromClientAsync(lobby, new AudioClipPlaybackReport { PlayId = first, Status = AudioClipPlaybackStatus.Stopped, Error = "Cut off by another clip" });

        Assert.AreEqual(second, s.Service.GetNowPlaying(lobby)!.PlayId);
    }

    [TestMethod]
    public async Task Stop_SendsTheCurrentPlayIdToTheKiosk()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);
        Guid playId = (await s.Service.PlayAsync(Request(s, lobby))).Data!.Single().PlayId!.Value;

        Result<bool> result = await s.Service.StopAsync(lobby, s.Company.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        (string method, object?[] args) = s.Sent["conn-Lobby"].Last();
        Assert.AreEqual(ClientHubMethods.StopAudioClip, method);
        Assert.AreEqual(playId, args.Single());
    }

    [TestMethod]
    public async Task Stop_WithNothingKnownPlaying_StopsWhateverIsPlaying()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        await s.Service.StopAsync(lobby, s.Company.Id);

        Assert.AreEqual(Guid.Empty, s.Sent["conn-Lobby"].Single().Args.Single());
    }

    [TestMethod]
    public async Task Stop_RefusesAnotherCompanysKiosk()
    {
        Setup s = await SeedAsync();
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        Result<bool> result = await s.Service.StopAsync(lobby, s.Company.Id + 1);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(0, s.Sent.Count);
    }
}
