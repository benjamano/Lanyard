using Lanyard.Application.Services;
using Lanyard.Application.Services.AudioClips;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Speech;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Lanyard.Tests.Services.Speech;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.AudioClips;

[TestClass]
public class AudioClipServiceTests
{
    private const string UserId = "user-1";

    private sealed class TestService(IDbContextFactory<ApplicationDbContext> factory, IFileService files, IAudioClipPlaybackService playback, ISecurityService security, ISpeechRenderService speech)
        : AudioClipService(factory, files, playback, security, speech, new SpeechPreviewStore(new MemoryCache(new MemoryCacheOptions())), NullLogger<AudioClipService>.Instance)
    {
        public HashSet<string> Online { get; } = [];

        protected override bool IsOnline(string? connectionId) => connectionId is not null && Online.Contains(connectionId);
    }

    private sealed class Setup
    {
        public required DbContextOptions<ApplicationDbContext> Options { get; init; }
        public required TestService Service { get; init; }
        public required Mock<IFileService> Files { get; init; }
        public required Mock<IAudioClipPlaybackService> Playback { get; init; }
        public required Mock<ISecurityService> Security { get; init; }
        public required Company Company { get; init; }
        public required Location Ipswich { get; init; }
        public required Location Norwich { get; init; }

        public LocationScope Admin => new(true, Ipswich.Id, Company.Id, Ipswich.Name);
        public LocationScope IpswichManager => new(false, Ipswich.Id, Company.Id, Ipswich.Name, IsManager: true);
        public LocationScope IpswichStaff => new(false, Ipswich.Id, Company.Id, Ipswich.Name, IsManager: false);
    }

    private static async Task<Setup> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options);

        Location norwich = new() { CompanyId = company.Id, Name = "Norwich", IsActive = true };
        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Locations.Add(norwich);
            await ctx.SaveChangesAsync();
        }

        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<IFileService> files = new();
        files.Setup(f => f.UploadFileAsync(It.IsAny<IFormFile>(), null, UserId, It.IsAny<FileUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IFormFile file, Guid? _, string _, FileUploadOptions _, CancellationToken _) =>
            {
                FileMetadata metadata = new() { Id = Guid.NewGuid(), CompanyId = company.Id, FileName = file.FileName, FilePath = file.FileName, FileSize = file.Length, IsActive = true };

                using ApplicationDbContext ctx = new(options);
                ctx.FileMetadata.Add(metadata);
                ctx.SaveChanges();

                return Result<FileMetadata>.Ok(metadata);
            });

        Mock<IAudioClipPlaybackService> playback = new();
        playback.Setup(p => p.PlayAsync(It.IsAny<AudioClipPlayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<List<AudioClipKioskOutcome>>.Ok([]));
        playback.Setup(p => p.StopAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(Result<bool>.Ok(true));

        files.Setup(f => f.DeleteFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
            {
                using ApplicationDbContext ctx = new(options);
                FileMetadata? stored = ctx.FileMetadata.Find(id);
                if (stored is not null)
                {
                    ctx.FileMetadata.Remove(stored);
                    ctx.SaveChanges();
                }

                return Result<bool>.Ok(true);
            });

        Mock<ISecurityService> security = new();

        SpeechRenderService speech = new([new FakeSpeechEngine()], new MemoryCache(new MemoryCacheOptions()), NullLogger<SpeechRenderService>.Instance);

        return new Setup
        {
            Options = options,
            Service = new TestService(factory.Object, files.Object, playback.Object, security.Object, speech),
            Files = files,
            Playback = playback,
            Security = security,
            Company = company,
            Ipswich = ipswich,
            Norwich = norwich
        };
    }

    private static IFormFile FormFile(string fileName, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName) { Headers = new HeaderDictionary(), ContentType = "audio/wav" };

    private static async Task<AudioClip> AddClipAsync(Setup s, string name, int? locationId, DateTime? deleted = null)
    {
        AudioClip clip = new() { Id = Guid.NewGuid(), CompanyId = s.Company.Id, Name = name, LocationId = locationId, DurationSeconds = 5, CreateDate = DateTime.UtcNow, DeleteDate = deleted };

        await using ApplicationDbContext ctx = new(s.Options);
        ctx.AudioClips.Add(clip);
        await ctx.SaveChangesAsync();

        return clip;
    }

    private static async Task<Guid> AddKioskAsync(Setup s, string name, Location location, string version = "1.0.42")
    {
        Guid id = Guid.NewGuid();

        await using ApplicationDbContext ctx = new(s.Options);
        ctx.Clients.Add(new Client { Id = id, CompanyId = s.Company.Id, Name = name, LocationId = location.Id, MostRecentConnectionId = $"conn-{name}", ReportedClientVersion = version });
        await ctx.SaveChangesAsync();

        return id;
    }

    [TestMethod]
    public async Task Upload_StoresTheFileOutsideTheMusicLibrary_AndAddsTheClip()
    {
        Setup s = await SeedAsync();

        Result<AudioClip> result = await s.Service.UploadAsync(s.Admin, FormFile("closing.wav", TestWav.OfSeconds(8)), new AudioClipDetails(" Closing time ", null), UserId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        s.Files.Verify(f => f.UploadFileAsync(It.IsAny<IFormFile>(), null, UserId, It.Is<FileUploadOptions>(o => !o.AddAudioToMusicLibrary), It.IsAny<CancellationToken>()), Times.Once);

        await using ApplicationDbContext ctx = new(s.Options);
        AudioClip clip = await ctx.AudioClips.SingleAsync();
        Assert.AreEqual("Closing time", clip.Name);
        Assert.AreEqual(8, clip.DurationSeconds, 0.1);
        Assert.IsNull(clip.LocationId);
        Assert.AreEqual(UserId, clip.CreatedByUserId);
        Assert.AreEqual(0, await ctx.Songs.CountAsync());
    }

    [TestMethod]
    public async Task Upload_RefusesAFileThatIsNotAudio_WithoutStoringIt()
    {
        Setup s = await SeedAsync();

        Result<AudioClip> result = await s.Service.UploadAsync(s.Admin, FormFile("closing.mp3", "not audio"u8.ToArray()), new AudioClipDetails("Closing", null), UserId);

        Assert.IsFalse(result.IsSuccess);
        s.Files.Verify(f => f.UploadFileAsync(It.IsAny<IFormFile>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<FileUploadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Upload_ByAManager_ForTheirOwnLocation_IsAllowed()
    {
        Setup s = await SeedAsync();

        Result<AudioClip> result = await s.Service.UploadAsync(s.IpswichManager, FormFile("party.wav", TestWav.OfSeconds(3)), new AudioClipDetails("Party", s.Ipswich.Id), UserId);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task Upload_ByAManager_ForEveryLocationOrAnotherLocation_IsRefused()
    {
        Setup s = await SeedAsync();

        Result<AudioClip> everywhere = await s.Service.UploadAsync(s.IpswichManager, FormFile("a.wav", TestWav.OfSeconds(3)), new AudioClipDetails("A", null), UserId);
        Result<AudioClip> elsewhere = await s.Service.UploadAsync(s.IpswichManager, FormFile("a.wav", TestWav.OfSeconds(3)), new AudioClipDetails("A", s.Norwich.Id), UserId);

        Assert.IsFalse(everywhere.IsSuccess);
        Assert.IsFalse(elsewhere.IsSuccess);
    }

    [TestMethod]
    public async Task Upload_ByStaff_IsRefused()
    {
        Setup s = await SeedAsync();

        Result<AudioClip> result = await s.Service.UploadAsync(s.IpswichStaff, FormFile("a.wav", TestWav.OfSeconds(3)), new AudioClipDetails("A", s.Ipswich.Id), UserId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    [DataRow("", 100, null)]
    [DataRow("Fine", 101, null)]
    [DataRow("Fine", 100, -1)]
    public async Task Upload_RefusesBadDetails(string name, int volume, int? duck)
    {
        Setup s = await SeedAsync();

        Result<AudioClip> result = await s.Service.UploadAsync(s.Admin, FormFile("a.wav", TestWav.OfSeconds(3)), new AudioClipDetails(name, null, volume, duck), UserId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task CreateFromFile_ChecksTheFilesAudio_AndAddsTheClip()
    {
        Setup s = await SeedAsync();
        byte[] bytes = TestWav.OfSeconds(6);
        FileMetadata file = new() { Id = Guid.NewGuid(), CompanyId = s.Company.Id, FileName = "safety.wav", FilePath = "safety.wav", FileSize = bytes.Length, IsActive = true };

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            ctx.FileMetadata.Add(file);
            await ctx.SaveChangesAsync();
        }

        s.Files.Setup(f => f.OpenFileContentAsync(file.Id, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Result<FileContent>.Ok(new FileContent { Stream = new MemoryStream(bytes), Metadata = file, TotalLength = bytes.Length }));

        Result<AudioClip> result = await s.Service.CreateFromFileAsync(s.Admin, file.Id, new AudioClipDetails("Safety", null), UserId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(file.Id, result.Data!.FileMetadataId);
        Assert.AreEqual(6, result.Data.DurationSeconds, 0.1);
    }

    [TestMethod]
    public async Task CreateFromFile_RefusesAFileOverTheSizeLimit_WithoutReadingIt()
    {
        Setup s = await SeedAsync();
        FileMetadata file = new() { Id = Guid.NewGuid(), CompanyId = s.Company.Id, FileName = "huge.wav", FilePath = "huge.wav", FileSize = AudioClipFileCheck.MaxBytes + 1, IsActive = true };

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            ctx.FileMetadata.Add(file);
            await ctx.SaveChangesAsync();
        }

        Result<AudioClip> result = await s.Service.CreateFromFileAsync(s.Admin, file.Id, new AudioClipDetails("Huge", null), UserId);

        Assert.IsFalse(result.IsSuccess);
        s.Files.Verify(f => f.OpenFileContentAsync(It.IsAny<Guid>(), It.IsAny<FileByteRange?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task List_ShowsAnAdminEveryClip_AndOthersOnlyTheirLocationsAndCompanyWideOnes()
    {
        Setup s = await SeedAsync();
        await AddClipAsync(s, "Everywhere", null);
        await AddClipAsync(s, "Ipswich", s.Ipswich.Id);
        await AddClipAsync(s, "Norwich", s.Norwich.Id);
        await AddClipAsync(s, "Deleted", null, deleted: DateTime.UtcNow);

        List<AudioClip> admin = (await s.Service.ListAsync(s.Admin)).Data!;
        List<AudioClip> staff = (await s.Service.ListAsync(s.IpswichStaff)).Data!;

        CollectionAssert.AreEqual(new[] { "Everywhere", "Ipswich", "Norwich" }, admin.Select(x => x.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "Everywhere", "Ipswich" }, staff.Select(x => x.Name).ToArray());
    }

    [TestMethod]
    public async Task Update_ByAManager_OfAnotherLocationsClip_IsRefused()
    {
        Setup s = await SeedAsync();
        AudioClip norwich = await AddClipAsync(s, "Norwich", s.Norwich.Id);

        Result<AudioClip> result = await s.Service.UpdateAsync(s.IpswichManager, norwich.Id, new AudioClipDetails("Mine now", s.Ipswich.Id));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Update_SavesTheDetails()
    {
        Setup s = await SeedAsync();
        AudioClip clip = await AddClipAsync(s, "Old", s.Ipswich.Id);

        Result<AudioClip> result = await s.Service.UpdateAsync(s.IpswichManager, clip.Id, new AudioClipDetails("New", s.Ipswich.Id, 80, 0, 250, 3000));

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(s.Options);
        AudioClip saved = await ctx.AudioClips.SingleAsync();
        Assert.AreEqual("New", saved.Name);
        Assert.AreEqual(80, saved.Volume);
        Assert.AreEqual(0, saved.DuckToPercent);
        Assert.AreEqual(250, saved.FadeDownMs);
        Assert.AreEqual(3000, saved.FadeUpMs);
        Assert.IsNotNull(saved.UpdateDate);
    }

    [TestMethod]
    public async Task Delete_IsSoft()
    {
        Setup s = await SeedAsync();
        AudioClip clip = await AddClipAsync(s, "Bye", null);

        Result<bool> result = await s.Service.DeleteAsync(s.Admin, clip.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(s.Options);
        Assert.IsNotNull((await ctx.AudioClips.SingleAsync()).DeleteDate);
        Assert.AreEqual(0, (await s.Service.ListAsync(s.Admin)).Data!.Count);
    }

    [TestMethod]
    public async Task Play_WithoutThePlayRole_IsRefused()
    {
        Setup s = await SeedAsync();
        AudioClip clip = await AddClipAsync(s, "Closing", null);
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(s.IpswichStaff, clip.Id, [lobby], UserId);

        Assert.IsFalse(result.IsSuccess);
        s.Playback.Verify(p => p.PlayAsync(It.IsAny<AudioClipPlayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Play_WithThePlayRole_SendsToTheKiosksAtTheirLocation()
    {
        Setup s = await SeedAsync();
        s.Security.Setup(x => x.IsCurrentUserInRoleAsync(AudioClipRoles.CanPlayAudioClips)).ReturnsAsync(true);
        AudioClip clip = await AddClipAsync(s, "Closing", null);
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(s.IpswichStaff, clip.Id, [lobby], UserId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        s.Playback.Verify(p => p.PlayAsync(It.Is<AudioClipPlayRequest>(r =>
            r.ClipId == clip.Id && r.ClientIds.SequenceEqual(new[] { lobby }) && r.CompanyId == s.Company.Id
            && r.Source == AudioClipPlaySource.Manual && r.TriggeredByUserId == UserId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Play_OnAnotherLocationsKiosk_IsRefusedForAManager()
    {
        Setup s = await SeedAsync();
        AudioClip clip = await AddClipAsync(s, "Closing", null);
        Guid norwichKiosk = await AddKioskAsync(s, "Bar", s.Norwich);

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(s.IpswichManager, clip.Id, [norwichKiosk], UserId);

        Assert.IsFalse(result.IsSuccess);
        s.Playback.Verify(p => p.PlayAsync(It.IsAny<AudioClipPlayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Play_AnotherLocationsClip_IsRefusedForAManager()
    {
        Setup s = await SeedAsync();
        AudioClip norwichClip = await AddClipAsync(s, "Norwich only", s.Norwich.Id);
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(s.IpswichManager, norwichClip.Id, [lobby], UserId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Play_ByAnAdmin_CanUseAnyLocationsKiosk()
    {
        Setup s = await SeedAsync();
        AudioClip clip = await AddClipAsync(s, "Closing", null);
        Guid norwichKiosk = await AddKioskAsync(s, "Bar", s.Norwich);

        Result<List<AudioClipKioskOutcome>> result = await s.Service.PlayAsync(s.Admin, clip.Id, [norwichKiosk], UserId);

        Assert.IsTrue(result.IsSuccess, result.Error);
    }

    [TestMethod]
    public async Task Stop_OnAnotherLocationsKiosk_IsRefusedForAManager()
    {
        Setup s = await SeedAsync();
        Guid norwichKiosk = await AddKioskAsync(s, "Bar", s.Norwich);

        Result<bool> result = await s.Service.StopAsync(s.IpswichManager, norwichKiosk);

        Assert.IsFalse(result.IsSuccess);
        s.Playback.Verify(p => p.StopAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public async Task ListKiosks_SaysWhichAreOnlineAndRecentEnough()
    {
        Setup s = await SeedAsync();
        await AddKioskAsync(s, "Lobby", s.Ipswich);
        await AddKioskAsync(s, "Old", s.Ipswich, version: "1.0.41");
        await AddKioskAsync(s, "Bar", s.Norwich);
        s.Service.Online.Add("conn-Lobby");
        s.Service.Online.Add("conn-Old");

        List<AudioClipKiosk> manager = (await s.Service.ListKiosksAsync(s.IpswichManager)).Data!;
        List<AudioClipKiosk> admin = (await s.Service.ListKiosksAsync(s.Admin)).Data!;

        CollectionAssert.AreEquivalent(new[] { "Lobby", "Old" }, manager.Select(x => x.Name).ToArray());
        Assert.AreEqual(3, admin.Count);
        Assert.IsTrue(manager.Single(x => x.Name == "Lobby") is { IsOnline: true, CanPlayClips: true });
        Assert.IsTrue(manager.Single(x => x.Name == "Old") is { IsOnline: true, CanPlayClips: false });
        Assert.IsFalse(admin.Single(x => x.Name == "Bar").IsOnline);
    }

    [TestMethod]
    public async Task ListRecentPlays_ShowsWhoPlayedWhat()
    {
        Setup s = await SeedAsync();
        AudioClip clip = await AddClipAsync(s, "Closing", null);
        Guid lobby = await AddKioskAsync(s, "Lobby", s.Ipswich);
        UserProfile ben = await SchedulingTestHelpers.SeedUserAsync(s.Options, s.Ipswich);

        await using (ApplicationDbContext ctx = new(s.Options))
        {
            ctx.AudioClipPlays.Add(new AudioClipPlay
            {
                Id = Guid.NewGuid(), CompanyId = s.Company.Id, AudioClipId = clip.Id, ClientId = lobby, Source = AudioClipPlaySource.Manual,
                TriggeredByUserId = ben.Id, Status = AudioClipPlayStatus.Finished, RequestedAtUtc = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        AudioClipPlaySummary play = (await s.Service.ListRecentPlaysAsync(s.IpswichManager)).Data!.Single();

        Assert.AreEqual("Closing", play.ClipName);
        Assert.AreEqual("Lobby", play.KioskName);
        StringAssert.StartsWith(play.TriggeredBy, "Ben");
    }

    private static AudioClipSpeech Words(string text, string voice = "piper-cori", double rate = 1.0, int pitch = 0) =>
        new(text, new SpeechVoiceSettings(voice, rate, pitch));

    [TestMethod]
    public async Task CreateSpeech_RendersTheWordsIntoAHiddenWavFile_AndAddsASpeechClip()
    {
        Setup s = await SeedAsync();

        Result<AudioClip> result = await s.Service.CreateSpeechAsync(s.Admin, Words("Red team,   two minutes left."), new AudioClipDetails("Two minute warning", null), UserId);

        Assert.IsTrue(result.IsSuccess, result.Error);
        s.Files.Verify(f => f.UploadFileAsync(
            It.Is<IFormFile>(x => x.FileName.EndsWith(".wav")),
            null,
            UserId,
            It.Is<FileUploadOptions>(o => o.IsSystemFile && !o.AddAudioToMusicLibrary),
            It.IsAny<CancellationToken>()), Times.Once);

        await using ApplicationDbContext ctx = new(s.Options);
        AudioClip clip = await ctx.AudioClips.SingleAsync();
        Assert.AreEqual(AudioClipKind.Speech, clip.Kind);
        Assert.AreEqual("Red team, two minutes left.", clip.SpeechText);
        Assert.AreEqual("piper-cori", clip.SpeechVoiceId);
        Assert.IsNotNull(clip.FileMetadataId);
        Assert.IsNotNull(clip.SpeechRenderKey);
        Assert.IsTrue(clip.DurationSeconds > 0);
    }

    [TestMethod]
    public async Task CreateSpeech_RefusesEmptyTextAnUnknownVoiceAndOutOfRangeSettings_WithoutStoringAnything()
    {
        Setup s = await SeedAsync();

        Assert.IsFalse((await s.Service.CreateSpeechAsync(s.Admin, Words("   "), new AudioClipDetails("A", null), UserId)).IsSuccess);
        Assert.IsFalse((await s.Service.CreateSpeechAsync(s.Admin, Words("Hello", voice: "nobody"), new AudioClipDetails("A", null), UserId)).IsSuccess);
        Assert.IsFalse((await s.Service.CreateSpeechAsync(s.Admin, Words("Hello", rate: 3), new AudioClipDetails("A", null), UserId)).IsSuccess);
        Assert.IsFalse((await s.Service.CreateSpeechAsync(s.Admin, Words("Hello", pitch: 20), new AudioClipDetails("A", null), UserId)).IsSuccess);
        Assert.IsFalse((await s.Service.CreateSpeechAsync(s.Admin, Words(new string('a', SpeechRules.MaxTextLength + 1)), new AudioClipDetails("A", null), UserId)).IsSuccess);

        s.Files.Verify(f => f.UploadFileAsync(It.IsAny<IFormFile>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<FileUploadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task CreateSpeech_FollowsTheSameLocationRulesAsUploads()
    {
        Setup s = await SeedAsync();

        Result<AudioClip> everywhere = await s.Service.CreateSpeechAsync(s.IpswichManager, Words("Hello"), new AudioClipDetails("A", null), UserId);
        Result<AudioClip> otherLocation = await s.Service.CreateSpeechAsync(s.IpswichManager, Words("Hello"), new AudioClipDetails("A", s.Norwich.Id), UserId);
        Result<AudioClip> own = await s.Service.CreateSpeechAsync(s.IpswichManager, Words("Hello"), new AudioClipDetails("A", s.Ipswich.Id), UserId);

        Assert.IsFalse(everywhere.IsSuccess);
        Assert.IsFalse(otherLocation.IsSuccess);
        Assert.IsTrue(own.IsSuccess, own.Error);
    }

    [TestMethod]
    public async Task UpdateSpeech_RendersAgainOnlyWhenTheWordsOrVoiceChange_AndDeletesTheOldAudio()
    {
        Setup s = await SeedAsync();
        AudioClip created = (await s.Service.CreateSpeechAsync(s.Admin, Words("Closing in ten minutes."), new AudioClipDetails("Closing", null), UserId)).Data!;
        Guid firstFile = created.FileMetadataId!.Value;

        Result<AudioClip> renamed = await s.Service.UpdateSpeechAsync(s.Admin, created.Id, Words("Closing in ten minutes."), new AudioClipDetails("Closing soon", null), UserId);
        Assert.IsTrue(renamed.IsSuccess, renamed.Error);
        Assert.AreEqual(firstFile, renamed.Data!.FileMetadataId);

        Result<AudioClip> reworded = await s.Service.UpdateSpeechAsync(s.Admin, created.Id, Words("Closing in five minutes.", pitch: -2), new AudioClipDetails("Closing soon", null), UserId);
        Assert.IsTrue(reworded.IsSuccess, reworded.Error);
        Assert.AreNotEqual(firstFile, reworded.Data!.FileMetadataId);

        s.Files.Verify(f => f.UploadFileAsync(It.IsAny<IFormFile>(), null, UserId, It.IsAny<FileUploadOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        s.Files.Verify(f => f.DeleteFileAsync(firstFile, It.IsAny<CancellationToken>()), Times.Once);

        await using ApplicationDbContext ctx = new(s.Options);
        AudioClip stored = await ctx.AudioClips.SingleAsync();
        Assert.AreEqual("Closing in five minutes.", stored.SpeechText);
        Assert.AreEqual(-2, stored.SpeechPitch);
    }

    [TestMethod]
    public async Task UpdateSpeech_RefusesAnUploadedClip()
    {
        Setup s = await SeedAsync();
        AudioClip clip = await AddClipAsync(s, "Jingle", null);

        Result<AudioClip> result = await s.Service.UpdateSpeechAsync(s.Admin, clip.Id, Words("Hello"), new AudioClipDetails("Jingle", null), UserId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task Delete_OfASpeechClip_DeletesItsRenderedAudio_ButNotAnUploadedClipsFile()
    {
        Setup s = await SeedAsync();
        AudioClip spoken = (await s.Service.CreateSpeechAsync(s.Admin, Words("Hello there."), new AudioClipDetails("Hello", null), UserId)).Data!;
        AudioClip uploaded = (await s.Service.UploadAsync(s.Admin, FormFile("jingle.wav", TestWav.OfSeconds(3)), new AudioClipDetails("Jingle", null), UserId)).Data!;

        Assert.IsTrue((await s.Service.DeleteAsync(s.Admin, spoken.Id)).IsSuccess);
        Assert.IsTrue((await s.Service.DeleteAsync(s.Admin, uploaded.Id)).IsSuccess);

        s.Files.Verify(f => f.DeleteFileAsync(spoken.FileMetadataId!.Value, It.IsAny<CancellationToken>()), Times.Once);
        s.Files.Verify(f => f.DeleteFileAsync(uploaded.FileMetadataId!.Value, It.IsAny<CancellationToken>()), Times.Never);

        await using ApplicationDbContext ctx = new(s.Options);
        AudioClip deleted = await ctx.AudioClips.SingleAsync(x => x.Id == spoken.Id);
        Assert.IsNull(deleted.SpeechText);
        Assert.AreEqual("Hello", deleted.Name);
    }

    [TestMethod]
    public async Task ListAudioFiles_LeavesOutASpeechClipsRenderedAudio()
    {
        Setup s = await SeedAsync();
        await s.Service.CreateSpeechAsync(s.Admin, Words("Hello there."), new AudioClipDetails("Hello", null), UserId);
        await s.Service.UploadAsync(s.Admin, FormFile("jingle.wav", TestWav.OfSeconds(3)), new AudioClipDetails("Jingle", null), UserId);

        Result<List<AudioClipFileOption>> files = await s.Service.ListAudioFilesAsync(s.Admin);

        Assert.IsTrue(files.IsSuccess, files.Error);
        Assert.AreEqual("jingle.wav", files.Data!.Single().FileName);
    }

    [TestMethod]
    public async Task PreviewSpeech_NeedsPermissionToPlayClips()
    {
        Setup s = await SeedAsync();
        s.Security.Setup(x => x.IsCurrentUserInRoleAsync(AudioClipRoles.CanPlayAudioClips)).ReturnsAsync(false);

        Result<Guid> staff = await s.Service.PreviewSpeechAsync(s.IpswichStaff, Words("Hello"), UserId);
        Result<Guid> manager = await s.Service.PreviewSpeechAsync(s.IpswichManager, Words("Hello"), UserId);

        Assert.IsFalse(staff.IsSuccess);
        Assert.IsTrue(manager.IsSuccess, manager.Error);
        s.Files.Verify(f => f.UploadFileAsync(It.IsAny<IFormFile>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<FileUploadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
