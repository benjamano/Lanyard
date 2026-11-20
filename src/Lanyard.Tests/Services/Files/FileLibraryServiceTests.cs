using Lanyard.Application.Services;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Files;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Files;

[TestClass]
public class FileLibraryServiceTests
{
    private DbContextOptions<ApplicationDbContext> _options = null!;
    private FileService _files = null!;
    private FileLibraryService _library = null!;
    private readonly List<string> _tempFiles = [];

    [TestInitialize]
    public void Setup()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(_options));

        Mock<ICurrentUserAccessor> user = new();
        user.Setup(x => x.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok("manager-1"));

        Mock<IWebHostEnvironment> environment = new();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Development);

        _files = new FileService(factory.Object, user.Object, Mock.Of<ISongAnalysisQueue>(), environment.Object);
        FileUsageService usage = new(factory.Object, NullLogger<FileUsageService>.Instance);
        _library = new FileLibraryService(factory.Object, _files, usage, user.Object, NullLogger<FileLibraryService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string path in _tempFiles.Where(File.Exists))
            File.Delete(path);
    }

    // ---------- Seeding ----------

    private async Task<Guid> SeedFolderAsync(string name, Guid? parent = null, bool shared = false, bool active = true, DateTime? deletedAt = null)
    {
        await using ApplicationDbContext db = new(_options);
        Folder folder = new() { Id = Guid.NewGuid(), Name = name, ParentFolderId = parent, IsSharedWithStaff = shared, IsActive = active, DeletedAt = deletedAt };
        db.Folders.Add(folder);
        await db.SaveChangesAsync();
        return folder.Id;
    }

    private async Task<Guid> SeedFileAsync(string name, Guid? folder = null, bool active = true, DateTime? deletedAt = null,
        bool restricted = false, bool system = false, long size = 10, string? contentType = null)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, "bytes");
        _tempFiles.Add(path);

        await using ApplicationDbContext db = new(_options);
        FileMetadata file = new()
        {
            Id = Guid.NewGuid(),
            FileName = name,
            FilePath = path,
            FileSize = size,
            ContentType = contentType ?? FileUploadPolicy.ContentTypeFor(name),
            UploadedBy = "manager-1",
            FolderId = folder,
            IsActive = active,
            DeletedAt = deletedAt,
            IsRestricted = restricted,
            IsSystemFile = system
        };
        db.FileMetadata.Add(file);
        await db.SaveChangesAsync();
        return file.Id;
    }

    private async Task<FileMetadata?> FileAsync(Guid id)
    {
        await using ApplicationDbContext db = new(_options);
        return await db.FileMetadata.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    private async Task<Folder?> FolderAsync(Guid id)
    {
        await using ApplicationDbContext db = new(_options);
        return await db.Folders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    private static IFormFile Upload(string name, string content = "hello")
    {
        MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(content));
        return new FormFile(stream, 0, stream.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = "application/octet-stream" };
    }

    private async Task<FileQueryResult> QueryAsync(FileQuery query)
    {
        Result<FileQueryResult> result = await _library.QueryAsync(query, CancellationToken.None);
        Assert.IsTrue(result.IsSuccess, result.Error);
        return result.Data!;
    }

    // ---------- Browsing ----------

    [TestMethod]
    public async Task Query_ListsOnlyLibraryFiles()
    {
        Guid visible = await SeedFileAsync("poster.png");
        await SeedFileAsync("passport.pdf", restricted: true);
        await SeedFileAsync("closing-time.mp3", system: true);
        await SeedFileAsync("binned.png", active: false, deletedAt: DateTime.UtcNow);

        FileQueryResult result = await QueryAsync(new FileQuery());

        CollectionAssert.AreEqual(new[] { visible }, result.Files.Select(x => x.Id).ToArray(), "Staff documents, audio clip sounds and binned files never show in Files.");
    }

    [TestMethod]
    public async Task Query_HidesFilesInsideABinnedFolder()
    {
        Guid binned = await SeedFolderAsync("Old", active: false, deletedAt: DateTime.UtcNow);
        Guid inside = await SeedFolderAsync("Inside", binned);
        await SeedFileAsync("deep.png", inside);

        FileQueryResult all = await QueryAsync(new FileQuery { AllFolders = true });
        Result<FileQueryResult> browse = await _library.QueryAsync(new FileQuery { FolderId = inside }, CancellationToken.None);

        Assert.AreEqual(0, all.Files.Count);
        Assert.IsFalse(browse.IsSuccess, "A folder under a binned folder is in the bin too.");
    }

    [TestMethod]
    public async Task Search_FindsFilesInEveryFolder_WithTheirPath()
    {
        Guid posters = await SeedFolderAsync("Posters");
        Guid christmas = await SeedFolderAsync("Christmas", posters);
        await SeedFileAsync("Santa poster.png", christmas);
        await SeedFileAsync("menu.pdf");

        FileQueryResult result = await QueryAsync(new FileQuery { AllFolders = true, Search = "POSTER" });

        Assert.AreEqual(1, result.Files.Count);
        Assert.AreEqual("Posters / Christmas", result.Files[0].FolderPath);
        Assert.AreEqual("Posters", result.Folders.Single().Name, "Folders whose name matches come back too.");
    }

    [TestMethod]
    public async Task TypeFilter_UsesTheExtension()
    {
        await SeedFileAsync("intro.mp4");
        await SeedFileAsync("logo.png");
        await SeedFileAsync("rota.xlsx");

        FileQueryResult videos = await QueryAsync(new FileQuery { AllFolders = true, TypeGroup = FileTypeGroup.Video });
        FileQueryResult documents = await QueryAsync(new FileQuery { AllFolders = true, TypeGroup = FileTypeGroup.Document });

        Assert.AreEqual("intro.mp4", videos.Files.Single().FileName);
        Assert.AreEqual("Excel spreadsheet", documents.Files.Single().TypeName);
    }

    [TestMethod]
    public async Task Sort_BySizeDescending()
    {
        await SeedFileAsync("small.png", size: 1);
        await SeedFileAsync("big.png", size: 100);
        await SeedFileAsync("medium.png", size: 50);

        FileQueryResult result = await QueryAsync(new FileQuery { Sort = FileSort.Size, Descending = true });

        CollectionAssert.AreEqual(new[] { "big.png", "medium.png", "small.png" }, result.Files.Select(x => x.FileName).ToArray());
    }

    // ---------- Shared with staff ----------

    [TestMethod]
    public async Task SharedOnly_SeesSharedFoldersAndTheirSubfolders_ButNotTheTopLevel()
    {
        Guid shared = await SeedFolderAsync("Menus", shared: true);
        Guid sub = await SeedFolderAsync("Kids", shared);
        Guid managersOnly = await SeedFolderAsync("Payroll");
        Guid sharedUnderPrivate = await SeedFolderAsync("Rota PDFs", managersOnly, shared: true);
        await SeedFileAsync("top.png");
        Guid kidsMenu = await SeedFileAsync("kids-menu.pdf", sub);
        await SeedFileAsync("payslips.pdf", managersOnly);

        FileQueryResult top = await QueryAsync(new FileQuery { SharedOnly = true });
        FileQueryResult all = await QueryAsync(new FileQuery { SharedOnly = true, AllFolders = true });
        Result<FileQueryResult> privateFolder = await _library.QueryAsync(new FileQuery { SharedOnly = true, FolderId = managersOnly }, CancellationToken.None);
        IReadOnlyList<FolderNode> tree = (await _library.GetTreeAsync(sharedOnly: true, CancellationToken.None)).Data!;

        CollectionAssert.AreEquivalent(new[] { "Menus", "Rota PDFs" }, top.Folders.Select(x => x.Name).ToArray());
        Assert.AreEqual(0, top.Files.Count, "Files at the top level are never shared.");
        CollectionAssert.AreEqual(new[] { kidsMenu }, all.Files.Select(x => x.Id).ToArray());
        Assert.IsFalse(privateFolder.IsSuccess);
        CollectionAssert.AreEquivalent(new[] { shared, sub, sharedUnderPrivate }, tree.Select(x => x.Id).ToArray());
        Assert.IsNull(tree.Single(x => x.Id == sharedUnderPrivate).ParentFolderId, "Its parent isn't shared, so to staff it's at the top.");
        Assert.IsTrue(tree.Single(x => x.Id == sub).IsSharedEffective);
        Assert.IsFalse(tree.Single(x => x.Id == sub).IsSharedWithStaff, "Inherited, not set on the subfolder itself.");
    }

    [TestMethod]
    public async Task SetFolderShared_TurnsSharingOnAndOff()
    {
        Guid folder = await SeedFolderAsync("Posters");

        Result<FolderNode> on = await _library.SetFolderSharedAsync(folder, true, CancellationToken.None);

        Assert.IsTrue(on.Data!.IsSharedEffective);
        Assert.IsTrue((await FolderAsync(folder))!.IsSharedWithStaff);

        await _library.SetFolderSharedAsync(folder, false, CancellationToken.None);
        Assert.IsFalse((await FolderAsync(folder))!.IsSharedWithStaff);
    }

    // ---------- Names ----------

    [TestMethod]
    public async Task Upload_WithANameInUse_GetsANumber()
    {
        Guid folder = await SeedFolderAsync("Posters");
        await SeedFileAsync("poster.png", folder);

        Result<FileMetadata> second = await _files.UploadFileAsync(Upload("poster.png"), folder, "manager-1", CancellationToken.None);
        Result<FileMetadata> third = await _files.UploadFileAsync(Upload("Poster.png"), folder, "manager-1", CancellationToken.None);
        Result<FileMetadata> elsewhere = await _files.UploadFileAsync(Upload("poster.png"), null, "manager-1", CancellationToken.None);
        _tempFiles.AddRange([second.Data!.FilePath, third.Data!.FilePath, elsewhere.Data!.FilePath]);

        Assert.AreEqual("poster (2).png", second.Data.FileName);
        Assert.AreEqual("Poster (3).png", third.Data.FileName);
        Assert.AreEqual("poster.png", elsewhere.Data.FileName, "Names only need to be unique within a folder.");
    }

    [TestMethod]
    public async Task Upload_StoresBytesOutsideAnyFolderPath()
    {
        Guid folder = await SeedFolderAsync("Posters");

        Result<FileMetadata> uploaded = await _files.UploadFileAsync(Upload("poster.png"), folder, "manager-1", CancellationToken.None);
        _tempFiles.Add(uploaded.Data!.FilePath);

        StringAssert.DoesNotMatch(uploaded.Data.FilePath, new System.Text.RegularExpressions.Regex(folder.ToString()),
            "Bytes mustn't live under the folder, or moving the file and deleting the folder would lose them.");
    }

    [TestMethod]
    public async Task Upload_IntoABinnedFolder_IsRefused()
    {
        Guid folder = await SeedFolderAsync("Old", active: false, deletedAt: DateTime.UtcNow);

        Result<FileMetadata> uploaded = await _files.UploadFileAsync(Upload("poster.png"), folder, "manager-1", CancellationToken.None);

        Assert.IsFalse(uploaded.IsSuccess);
    }

    [TestMethod]
    public async Task Rename_ToANameInUse_IsRefused_IgnoringCase()
    {
        await SeedFileAsync("menu.pdf");
        Guid other = await SeedFileAsync("draft.pdf");

        Result<FileMetadata> renamed = await _files.RenameFileAsync(other, "MENU.pdf", CancellationToken.None);
        Result<FileMetadata> bad = await _files.RenameFileAsync(other, "a/b.pdf", CancellationToken.None);

        Assert.IsFalse(renamed.IsSuccess);
        StringAssert.Contains(renamed.Error, "already a file called");
        Assert.IsFalse(bad.IsSuccess);
    }

    [TestMethod]
    public async Task CreateFolder_ToANameInUse_IsRefused()
    {
        await SeedFolderAsync("Posters");

        Result<Folder> created = await _files.CreateFolderAsync("  posters ", null, "manager-1", CancellationToken.None);

        Assert.IsFalse(created.IsSuccess);
    }

    // ---------- Moving ----------

    [TestMethod]
    public async Task Move_AFolderIntoItsOwnSubfolder_IsRefused()
    {
        Guid parent = await SeedFolderAsync("Posters");
        Guid child = await SeedFolderAsync("Christmas", parent);

        BulkOperationResult result = (await _library.MoveAsync([], [parent], child, CancellationToken.None)).Data!;

        Assert.AreEqual(0, result.Succeeded);
        StringAssert.Contains(result.Failures.Single().Reason, "inside itself");
        Assert.IsNull((await FolderAsync(parent))!.ParentFolderId);
    }

    [TestMethod]
    public async Task Move_ReportsNameClashes_AndMovesTheRest()
    {
        Guid target = await SeedFolderAsync("Target");
        await SeedFileAsync("logo.png", target);
        Guid clash = await SeedFileAsync("logo.png");
        Guid fine = await SeedFileAsync("menu.pdf");

        BulkOperationResult result = (await _library.MoveAsync([clash, fine], [], target, CancellationToken.None)).Data!;

        Assert.AreEqual(1, result.Succeeded);
        Assert.AreEqual(clash, result.Failures.Single().Id);
        Assert.AreEqual(target, (await FileAsync(fine))!.FolderId);
        Assert.IsNull((await FileAsync(clash))!.FolderId);
    }

    // ---------- Recycle bin ----------

    [TestMethod]
    public async Task Bin_AFolder_TakesItsContentsWithIt_AndRestoringBringsThemBack()
    {
        Guid folder = await SeedFolderAsync("Halloween");
        Guid sub = await SeedFolderAsync("Pumpkins", folder);
        await SeedFileAsync("a.png", folder);
        await SeedFileAsync("b.png", sub);

        await _library.MoveToBinAsync([], [folder], CancellationToken.None);

        Assert.AreEqual(0, (await QueryAsync(new FileQuery { AllFolders = true })).Files.Count);
        BinItem item = (await _library.ListBinAsync(CancellationToken.None)).Data!.Single();
        Assert.AreEqual(BinItemKind.Folder, item.Kind);
        Assert.AreEqual(3, item.ItemCount, "Two files and the subfolder.");
        Assert.AreEqual("manager-1", (await FolderAsync(folder))!.DeletedByUserId);

        BulkOperationResult restored = (await _library.RestoreAsync([], [folder], CancellationToken.None)).Data!;

        Assert.AreEqual(1, restored.Succeeded);
        Assert.AreEqual(2, (await QueryAsync(new FileQuery { AllFolders = true })).Files.Count);
        Assert.AreEqual(0, (await _library.ListBinAsync(CancellationToken.None)).Data!.Count);
    }

    [TestMethod]
    public async Task Restore_AFileWhoseFolderIsStillInTheBin_PutsItAtTheTopLevel()
    {
        Guid folder = await SeedFolderAsync("Old");
        Guid file = await SeedFileAsync("keep.png", folder);

        await _library.MoveToBinAsync([file], [], CancellationToken.None);
        await _library.MoveToBinAsync([], [folder], CancellationToken.None);

        BulkOperationResult restored = (await _library.RestoreAsync([file], [], CancellationToken.None)).Data!;

        Assert.IsNull((await FileAsync(file))!.FolderId);
        StringAssert.Contains(restored.Notes.Single(), "top level");
    }

    [TestMethod]
    public async Task Restore_IntoANameNowInUse_RenamesIt()
    {
        Guid file = await SeedFileAsync("menu.pdf");
        await _library.MoveToBinAsync([file], [], CancellationToken.None);
        await SeedFileAsync("menu.pdf");

        BulkOperationResult restored = (await _library.RestoreAsync([file], [], CancellationToken.None)).Data!;

        Assert.AreEqual("menu (2).pdf", (await FileAsync(file))!.FileName);
        StringAssert.Contains(restored.Notes.Single(), "menu (2).pdf");
    }

    [TestMethod]
    public async Task DeleteForever_RefusesALiveFile()
    {
        Guid file = await SeedFileAsync("live.png");

        BulkOperationResult result = (await _library.DeleteForeverAsync([file], [], CancellationToken.None)).Data!;

        StringAssert.Contains(result.Failures.Single().Reason, "Recycle bin first");
        Assert.IsNotNull(await FileAsync(file));
    }

    [TestMethod]
    public async Task DeleteForever_RefusesAFileStillInUse()
    {
        Guid logo = await SeedFileAsync("logo.png");

        await using (ApplicationDbContext db = new(_options))
        {
            db.Companies.Add(new Company { Id = 7, Name = "Play2Day", LogoFileId = logo });
            await db.SaveChangesAsync();
        }

        await _library.MoveToBinAsync([logo], [], CancellationToken.None);
        BulkOperationResult result = (await _library.DeleteForeverAsync([logo], [], CancellationToken.None)).Data!;

        StringAssert.Contains(result.Failures.Single().Reason, "Play2Day's logo");
        Assert.IsNotNull(await FileAsync(logo));
    }

    [TestMethod]
    public async Task DeleteForever_RemovesTheRowAndTheBytes()
    {
        Guid file = await SeedFileAsync("old.png");
        string path = (await FileAsync(file))!.FilePath;
        await _library.MoveToBinAsync([file], [], CancellationToken.None);

        BulkOperationResult result = (await _library.DeleteForeverAsync([file], [], CancellationToken.None)).Data!;

        Assert.AreEqual(1, result.Succeeded);
        Assert.IsNull(await FileAsync(file));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task DeleteForever_AFolder_RemovesItsWholeSubtree()
    {
        Guid folder = await SeedFolderAsync("Old");
        Guid sub = await SeedFolderAsync("Older", folder);
        Guid a = await SeedFileAsync("a.png", folder);
        Guid b = await SeedFileAsync("b.png", sub);
        await _library.MoveToBinAsync([], [folder], CancellationToken.None);

        BulkOperationResult result = (await _library.DeleteForeverAsync([], [folder], CancellationToken.None)).Data!;

        Assert.AreEqual(1, result.Succeeded, string.Join(", ", result.Failures.Select(x => x.Reason)));
        Assert.IsNull(await FolderAsync(folder));
        Assert.IsNull(await FolderAsync(sub));
        Assert.IsNull(await FileAsync(a));
        Assert.IsNull(await FileAsync(b));
    }

    // The bug this rebuild started from: a file moved out of a folder kept its bytes under the
    // folder's storage prefix, and deleting the folder wiped them.
    [TestMethod]
    public async Task AFileMovedOutOfAFolder_SurvivesThatFolderBeingDeletedForGood()
    {
        Guid folder = await SeedFolderAsync("Old");
        Result<FileMetadata> uploaded = await _files.UploadFileAsync(Upload("keep.png", "precious"), folder, "manager-1", CancellationToken.None);
        _tempFiles.Add(uploaded.Data!.FilePath);

        await _library.MoveAsync([uploaded.Data.Id], [], null, CancellationToken.None);
        await _library.MoveToBinAsync([], [folder], CancellationToken.None);
        await _library.DeleteForeverAsync([], [folder], CancellationToken.None);

        Result<FileContent> opened = await _files.OpenFileContentAsync(uploaded.Data.Id, null, CancellationToken.None);
        Assert.IsTrue(opened.IsSuccess, opened.Error);
        await using FileContent content = opened.Data!;
        using StreamReader reader = new(content.Stream);
        Assert.AreEqual("precious", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task PurgeExpired_RemovesOnlyItemsPastThirtyDays_AndKeepsAnythingInUse()
    {
        DateTime now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        Guid expired = await SeedFileAsync("expired.png", active: false, deletedAt: now.AddDays(-31));
        Guid recent = await SeedFileAsync("recent.png", active: false, deletedAt: now.AddDays(-29));
        Guid inUse = await SeedFileAsync("intro.mp4", active: false, deletedAt: now.AddDays(-40));
        Guid expiredFolder = await SeedFolderAsync("Old", active: false, deletedAt: now.AddDays(-31));

        await using (ApplicationDbContext db = new(_options))
        {
            Guid programId = Guid.NewGuid();
            Guid stepId = Guid.NewGuid();
            db.ProjectionPrograms.Add(new ProjectionProgram { Id = programId, Name = "Birthday intro", IsActive = true });
            db.ProjectionProgramSteps.Add(new ProjectionProgramStep
            {
                Id = stepId,
                ProjectionProgramId = programId,
                TemplateId = Guid.NewGuid(),
                IsActive = true,
                ParameterValues = [new ProjectionProgramParameterValue { Id = Guid.NewGuid(), ProjectionProgramStepId = stepId, ParameterId = Guid.NewGuid(), Value = inUse.ToString() }]
            });
            await db.SaveChangesAsync();
        }

        BulkOperationResult result = (await _library.PurgeExpiredAsync(now, CancellationToken.None)).Data!;

        Assert.AreEqual(2, result.Succeeded);
        Assert.IsNull(await FileAsync(expired));
        Assert.IsNull(await FolderAsync(expiredFolder));
        Assert.IsNotNull(await FileAsync(recent));
        Assert.IsNotNull(await FileAsync(inUse));
        StringAssert.Contains(result.Failures.Single().Reason, "Birthday intro");
    }

    [TestMethod]
    public async Task EmptyBin_DeletesEverythingInIt()
    {
        await SeedFileAsync("a.png", active: false, deletedAt: DateTime.UtcNow);
        await SeedFolderAsync("Old", active: false, deletedAt: DateTime.UtcNow);
        Guid live = await SeedFileAsync("live.png");

        BulkOperationResult result = (await _library.EmptyBinAsync(CancellationToken.None)).Data!;

        Assert.AreEqual(2, result.Succeeded);
        Assert.AreEqual(0, (await _library.ListBinAsync(CancellationToken.None)).Data!.Count);
        Assert.IsNotNull(await FileAsync(live));
    }

    // ---------- Usage and zips ----------

    [TestMethod]
    public async Task Usage_FindsCourseImagesAndAudioClips()
    {
        Guid image = await SeedFileAsync("fire-exit.png");
        Guid sound = await SeedFileAsync("closing.mp3");
        Guid unused = await SeedFileAsync("unused.png");

        await using (ApplicationDbContext db = new(_options))
        {
            Guid courseId = Guid.NewGuid();
            db.Courses.Add(new Course { Id = courseId, Name = "Fire safety", IsActive = true });
            db.CourseSections.Add(new CourseSection { Id = Guid.NewGuid(), CourseId = courseId, Title = "Exits", IsActive = true, BodyHtml = $"<p><img src=\"/api/files/download/{image}\"></p>" });
            db.AudioClips.Add(new AudioClip { Id = Guid.NewGuid(), Name = "Closing time", FileMetadataId = sound, CreateDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        FileUsageService usage = new(MockFactory(), NullLogger<FileUsageService>.Instance);
        IReadOnlyDictionary<Guid, IReadOnlyList<FileUsage>> found = (await usage.GetUsagesAsync([image, sound, unused], CancellationToken.None)).Data!;

        StringAssert.Contains(found[image].Single().Description, "Fire safety");
        Assert.AreEqual(FileUsageKind.AudioClip, found[sound].Single().Kind);
        Assert.IsFalse(found.ContainsKey(unused));

        FileQueryResult listed = await QueryAsync(new FileQuery());
        Assert.IsTrue(listed.Files.Single(x => x.Id == image).IsInUse);
        Assert.IsFalse(listed.Files.Single(x => x.Id == unused).IsInUse);
    }

    [TestMethod]
    public async Task PlanZip_KeepsFolderLayout_AndNumbersClashingNames()
    {
        Guid posters = await SeedFolderAsync("Posters");
        Guid christmas = await SeedFolderAsync("Christmas", posters);
        await SeedFileAsync("a.png", posters);
        await SeedFileAsync("b.png", christmas);
        Guid loose1 = await SeedFileAsync("menu.pdf");
        Guid other = await SeedFolderAsync("Other");
        Guid loose2 = await SeedFileAsync("menu.pdf", other);

        IReadOnlyList<ZipEntryPlan> plan = (await _library.PlanZipAsync([loose1, loose2], [posters], sharedOnly: false, CancellationToken.None)).Data!;

        CollectionAssert.AreEquivalent(
            new[] { "Posters/a.png", "Posters/Christmas/b.png", "menu.pdf", "menu (2).pdf" },
            plan.Select(x => x.EntryPath).ToArray());
    }

    [TestMethod]
    public async Task PlanZip_ForStaff_LeavesOutFoldersThatArentShared()
    {
        Guid shared = await SeedFolderAsync("Menus", shared: true);
        Guid hidden = await SeedFolderAsync("Payroll");
        await SeedFileAsync("menu.pdf", shared);
        Guid payslip = await SeedFileAsync("payslip.pdf", hidden);

        IReadOnlyList<ZipEntryPlan> plan = (await _library.PlanZipAsync([payslip], [shared, hidden], sharedOnly: true, CancellationToken.None)).Data!;

        CollectionAssert.AreEqual(new[] { "Menus/menu.pdf" }, plan.Select(x => x.EntryPath).ToArray());
    }

    // ---------- Review fixes ----------

    private FileLibraryService LibraryFor(params string[] roles)
    {
        System.Security.Claims.ClaimsIdentity identity = new([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "someone")], "test");

        foreach (string role in roles)
            identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));

        Mock<IHttpContextAccessor> http = new();
        http.SetupGet(x => x.HttpContext).Returns(new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) });

        Mock<ICurrentUserAccessor> user = new();
        user.Setup(x => x.GetCurrentUserIdAsync()).ReturnsAsync(Result<string>.Ok("someone"));

        return new FileLibraryService(MockFactory(), _files, new FileUsageService(MockFactory(), NullLogger<FileUsageService>.Instance), user.Object,
            NullLogger<FileLibraryService>.Instance, null, http.Object, new Lanyard.Infrastructure.DataAccess.Tenancy.FixedTenantProvider(1));
    }

    [TestMethod]
    public async Task SomeoneWhoDoesntManageFiles_CantChangeAnything()
    {
        Guid folder = await SeedFolderAsync("Menus", shared: true);
        Guid file = await SeedFileAsync("menu.pdf", folder);
        FileLibraryService staff = LibraryFor("Staff");

        Assert.IsFalse((await staff.MoveToBinAsync([file], [], CancellationToken.None)).IsSuccess);
        Assert.IsFalse((await staff.MoveAsync([file], [], null, CancellationToken.None)).IsSuccess);
        Assert.IsFalse((await staff.SetFolderSharedAsync(folder, false, CancellationToken.None)).IsSuccess);
        Assert.IsFalse((await staff.EmptyBinAsync(CancellationToken.None)).IsSuccess);
        Assert.IsFalse((await staff.ListBinAsync(CancellationToken.None)).IsSuccess);
        Assert.IsTrue((await FileAsync(file))!.IsActive);

        Assert.IsTrue((await LibraryFor("CanManageFiles").MoveToBinAsync([file], [], CancellationToken.None)).IsSuccess);
    }

    [TestMethod]
    public async Task ADeletedFile_CantBeOpened_UnlessSomethingStillUsesIt()
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(_options));
        Mock<IWebHostEnvironment> environment = new();
        environment.SetupGet(x => x.EnvironmentName).Returns(Environments.Development);
        FileService files = new(factory.Object, Mock.Of<ICurrentUserAccessor>(), Mock.Of<ISongAnalysisQueue>(), environment.Object,
            usage: new FileUsageService(factory.Object, NullLogger<FileUsageService>.Instance));

        Guid unused = await SeedFileAsync("old.png", active: false, deletedAt: DateTime.UtcNow);
        Guid logo = await SeedFileAsync("logo.png", active: false, deletedAt: DateTime.UtcNow);
        Guid binnedFolder = await SeedFolderAsync("Old", active: false, deletedAt: DateTime.UtcNow);
        Guid inFolder = await SeedFileAsync("inside.png", binnedFolder);
        Guid live = await SeedFileAsync("live.png");

        await using (ApplicationDbContext db = new(_options))
        {
            db.Companies.Add(new Company { Id = 9, Name = "Play2Day", LogoFileId = logo });
            await db.SaveChangesAsync();
        }

        async Task<bool> Opens(Guid id)
        {
            Result<FileContent> opened = await files.OpenFileContentAsync(id, null, CancellationToken.None);

            if (opened.Data is FileContent content)
                await content.DisposeAsync();

            return opened.IsSuccess;
        }

        Assert.IsFalse(await Opens(unused), "Deleted and unused: gone.");
        Assert.IsFalse(await Opens(inFolder), "In a deleted folder counts as deleted.");
        Assert.IsTrue(await Opens(logo), "Still the logo, so it keeps working.");
        Assert.IsTrue(await Opens(live));
    }

    [TestMethod]
    public async Task FileIdsInFolders_IncludesSubfolders()
    {
        Guid parent = await SeedFolderAsync("Posters");
        Guid child = await SeedFolderAsync("Christmas", parent);
        Guid a = await SeedFileAsync("a.png", parent);
        Guid b = await SeedFileAsync("b.png", child);
        await SeedFileAsync("elsewhere.png");

        IReadOnlyList<Guid> ids = (await _library.GetFileIdsInFoldersAsync([parent], CancellationToken.None)).Data!;

        CollectionAssert.AreEquivalent(new[] { a, b }, ids.ToArray());
    }

    private IDbContextFactory<ApplicationDbContext> MockFactory()
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(_options));
        return factory.Object;
    }
}

[TestClass]
public class FileNameRulesTests
{
    [TestMethod]
    public void MakeUnique_CountsOnFromAnExistingNumber()
    {
        Assert.AreEqual("poster.png", FileNameRules.MakeUnique("poster.png", []));
        Assert.AreEqual("poster (2).png", FileNameRules.MakeUnique("poster.png", ["POSTER.png"]));
        Assert.AreEqual("poster (3).png", FileNameRules.MakeUnique("poster (2).png", ["poster.png", "poster (2).png"]));
        Assert.AreEqual("Menus (2)", FileNameRules.MakeUnique("Menus", ["menus"]));
    }

    [TestMethod]
    public void Validate_RefusesSlashesEmptyAndDots()
    {
        Assert.IsNotNull(FileNameRules.Validate("a/b.png"));
        Assert.IsNotNull(FileNameRules.Validate("   "));
        Assert.IsNotNull(FileNameRules.Validate(".."));
        Assert.IsNotNull(FileNameRules.Validate(new string('a', 256)));
        Assert.IsNull(FileNameRules.Validate("Santa's grotto (final).png"));
    }

    [TestMethod]
    public void TypeInfo_GivesFriendlyNames()
    {
        Assert.AreEqual("PNG image", FileTypeInfo.FriendlyName("x.PNG"));
        Assert.AreEqual(FileTypeGroup.Video, FileTypeInfo.GroupOf("intro.mov"));
        Assert.AreEqual("XYZ file", FileTypeInfo.FriendlyName("odd.xyz"));
        Assert.AreEqual("1.5 MB", FileTypeInfo.FormatSize(1536 * 1024));
    }
}
