using System.Security.Cryptography;
using System.Text;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Gdpr;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Gdpr;

[TestClass]
public class GdprServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    // Mirrors SecurityServiceTests.BuildUserManager - a real Identity DI container is needed so
    // UserManager.DeleteAsync/CreateAsync work against the InMemory provider.
    private static UserManager<UserProfile> BuildUserManager(DbContextOptions<ApplicationDbContext> options)
    {
        ServiceCollection services = new();

        services.AddSingleton(options);
        services.AddScoped(sp => new ApplicationDbContext(sp.GetRequiredService<DbContextOptions<ApplicationDbContext>>()));
        services.AddDataProtection();
        services.AddLogging();

        services.AddIdentityCore<UserProfile>(o => o.Password.RequireNonAlphanumeric = false)
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<UserManager<UserProfile>>();
    }

    private static Mock<ISecurityService> BuildSecurityServiceMock(bool isAdmin, UserProfile performingAdmin)
    {
        Mock<ISecurityService> mock = new();
        mock.Setup(s => s.IsCurrentUserInRoleAsync("Admin")).ReturnsAsync(isAdmin);
        mock.Setup(s => s.GetCurrentUserProfileAsync()).ReturnsAsync(Result<UserProfile>.Ok(performingAdmin));
        return mock;
    }

    private static GdprService BuildService(
        DbContextOptions<ApplicationDbContext> options,
        UserManager<UserProfile> userManager,
        Mock<ISecurityService> securityServiceMock)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new GdprService(factoryMock.Object, userManager, securityServiceMock.Object, NullLogger<GdprService>.Instance);
    }

    private static async Task<UserProfile> SeedUserAsync(
        UserManager<UserProfile> userManager, string firstName = "Jane", string lastName = "Doe", string? email = "jane@example.com")
    {
        UserProfile user = new()
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email ?? Guid.NewGuid().ToString(),
            Email = email,
            FirstName = firstName,
            LastName = lastName
        };

        IdentityResult result = await userManager.CreateAsync(user);
        Assert.IsTrue(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        return user;
    }

    [TestMethod]
    public async Task EraseUserDataAsync_NonAdmin_Fails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        UserManager<UserProfile> userManager = BuildUserManager(options);

        UserProfile admin = await SeedUserAsync(userManager, "Ada", "Min", "admin@example.com");
        UserProfile target = await SeedUserAsync(userManager);

        Mock<ISecurityService> securityServiceMock = BuildSecurityServiceMock(isAdmin: false, admin);
        GdprService service = BuildService(options, userManager, securityServiceMock);

        Result<bool> result = await service.EraseUserDataAsync(target.Id);

        Assert.IsFalse(result.IsSuccess);
        Assert.Contains("administrator", result.Error);
        Assert.IsNotNull(await userManager.FindByIdAsync(target.Id));
    }

    [TestMethod]
    public async Task EraseUserDataAsync_PlaceholderAccount_Fails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        UserManager<UserProfile> userManager = BuildUserManager(options);

        UserProfile admin = await SeedUserAsync(userManager, "Ada", "Min", "admin@example.com");

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Users.Add(new UserProfile { Id = ApplicationDbContext.SystemDeletedUserPlaceholderId, UserName = "deleted-user" });
            await ctx.SaveChangesAsync();
        }

        Mock<ISecurityService> securityServiceMock = BuildSecurityServiceMock(isAdmin: true, admin);
        GdprService service = BuildService(options, userManager, securityServiceMock);

        Result<bool> result = await service.EraseUserDataAsync(ApplicationDbContext.SystemDeletedUserPlaceholderId);

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task EraseUserDataAsync_Admin_WritesAuditRecordAndDeletesAccount()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        UserManager<UserProfile> userManager = BuildUserManager(options);

        UserProfile admin = await SeedUserAsync(userManager, "Ada", "Min", "admin@example.com");
        UserProfile target = await SeedUserAsync(userManager);

        Mock<ISecurityService> securityServiceMock = BuildSecurityServiceMock(isAdmin: true, admin);
        GdprService service = BuildService(options, userManager, securityServiceMock);

        Result<bool> result = await service.EraseUserDataAsync(target.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        UserErasureRecord? record = await ctx.UserErasureRecords.FirstOrDefaultAsync(x => x.ErasedUserId == target.Id);

        Assert.IsNotNull(record);
        Assert.AreEqual(admin.Id, record.PerformedByUserId);
        Assert.AreEqual(admin.GetName(), record.PerformedByUserName);

        string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.NormalizedEmail!.ToUpperInvariant())));
        Assert.AreEqual(expectedHash, record.ErasedEmailHash);

        Assert.IsNull(await userManager.FindByIdAsync(target.Id));
    }

    [TestMethod]
    public async Task EraseUserDataAsync_DownstreamFailure_AuditRecordSurvives_AccountNotDeleted()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        UserManager<UserProfile> userManager = BuildUserManager(options);

        UserProfile admin = await SeedUserAsync(userManager, "Ada", "Min", "admin@example.com");
        UserProfile target = await SeedUserAsync(userManager);

        Mock<ISecurityService> securityServiceMock = BuildSecurityServiceMock(isAdmin: true, admin);

        // First CreateDbContextAsync call is the audit write, which must succeed and commit; the
        // second is the anonymization pass, forced to fail here to prove the audit row (and the
        // still-intact account) survive a partial failure - the whole point of writing it first.
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.SetupSequence(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options))
            .ThrowsAsync(new InvalidOperationException("simulated downstream failure"));

        GdprService service = new(factoryMock.Object, userManager, securityServiceMock.Object, NullLogger<GdprService>.Instance);

        Result<bool> result = await service.EraseUserDataAsync(target.Id);

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(options);
        UserErasureRecord? record = await ctx.UserErasureRecords.FirstOrDefaultAsync(x => x.ErasedUserId == target.Id);
        Assert.IsNotNull(record);

        Assert.IsNotNull(await userManager.FindByIdAsync(target.Id));
    }

    [TestMethod]
    public async Task EraseUserDataAsync_AnonymizesNullableAttributionFKs_KeepsParentRows()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        UserManager<UserProfile> userManager = BuildUserManager(options);

        UserProfile admin = await SeedUserAsync(userManager, "Ada", "Min", "admin@example.com");
        UserProfile target = await SeedUserAsync(userManager);

        Guid playlistId = Guid.NewGuid();
        Guid courseId = Guid.NewGuid();
        Guid assignmentId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Playlists.Add(new Playlist
            {
                Id = playlistId,
                Name = "Test Playlist",
                CreateByUserId = target.Id,
                DeleteByUserId = target.Id,
                CreateDate = DateTime.UtcNow,
                DeleteDate = DateTime.UtcNow
            });

            ctx.Courses.Add(new Course { Id = courseId, Name = "Induction", IsActive = true });

            ctx.CourseAssignments.Add(new CourseAssignment
            {
                Id = assignmentId,
                CourseId = courseId,
                UserId = admin.Id,
                AssignedByUserId = target.Id,
                AssignedDate = DateTime.UtcNow,
                IsActive = true
            });

            await ctx.SaveChangesAsync();
        }

        Mock<ISecurityService> securityServiceMock = BuildSecurityServiceMock(isAdmin: true, admin);
        GdprService service = BuildService(options, userManager, securityServiceMock);

        Result<bool> result = await service.EraseUserDataAsync(target.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext verifyCtx = new(options);

        Playlist? playlist = await verifyCtx.Playlists.FindAsync(playlistId);
        Assert.IsNotNull(playlist);
        Assert.IsNull(playlist.CreateByUserId);
        Assert.IsNull(playlist.DeleteByUserId);

        CourseAssignment? assignment = await verifyCtx.CourseAssignments.FindAsync(assignmentId);
        Assert.IsNotNull(assignment);
        Assert.IsNull(assignment.AssignedByUserId);
    }

    [TestMethod]
    public async Task EraseUserDataAsync_RepointsNonNullableAttributionFKs_ToPlaceholder_KeepsParentRows()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        UserManager<UserProfile> userManager = BuildUserManager(options);

        UserProfile admin = await SeedUserAsync(userManager, "Ada", "Min", "admin@example.com");
        UserProfile target = await SeedUserAsync(userManager);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Users.Add(new UserProfile { Id = ApplicationDbContext.SystemDeletedUserPlaceholderId, UserName = "deleted-user" });
            await ctx.SaveChangesAsync();
        }

        Guid sceneId = Guid.NewGuid();
        string roleId = Guid.NewGuid().ToString();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.DmxScenes.Add(new DmxScene
            {
                Id = sceneId,
                ClientId = Guid.NewGuid(),
                Name = "Test Scene",
                CreateByUserId = target.Id,
                CreateDate = DateTime.UtcNow
            });

            ctx.Roles.Add(new ApplicationRole
            {
                Id = roleId,
                Name = "TestRole",
                NormalizedName = "TESTROLE",
                ConcurrencyStamp = Guid.NewGuid().ToString(),
                CreatedByUserId = target.Id,
                CreateDate = DateTime.UtcNow,
                IsActive = true
            });

            await ctx.SaveChangesAsync();
        }

        Mock<ISecurityService> securityServiceMock = BuildSecurityServiceMock(isAdmin: true, admin);
        GdprService service = BuildService(options, userManager, securityServiceMock);

        Result<bool> result = await service.EraseUserDataAsync(target.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext verifyCtx = new(options);

        DmxScene? scene = await verifyCtx.DmxScenes.FindAsync(sceneId);
        Assert.IsNotNull(scene);
        Assert.AreEqual(ApplicationDbContext.SystemDeletedUserPlaceholderId, scene.CreateByUserId);

        ApplicationRole? role = await verifyCtx.Roles.FindAsync(roleId);
        Assert.IsNotNull(role);
        Assert.AreEqual(ApplicationDbContext.SystemDeletedUserPlaceholderId, role.CreatedByUserId);
    }

    [TestMethod]
    public async Task EraseUserDataAsync_RemovesOwnedRecords()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        UserManager<UserProfile> userManager = BuildUserManager(options);

        UserProfile admin = await SeedUserAsync(userManager, "Ada", "Min", "admin@example.com");
        UserProfile target = await SeedUserAsync(userManager);

        Guid courseId = Guid.NewGuid();
        Guid assignmentId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            Company company = new() { Name = "Play2Day", IsActive = true };
            ctx.Companies.Add(company);
            await ctx.SaveChangesAsync();

            Location location = new() { CompanyId = company.Id, Name = "Ipswich", IsActive = true };
            ctx.Locations.Add(location);
            await ctx.SaveChangesAsync();

            ctx.Courses.Add(new Course { Id = courseId, Name = "Induction", IsActive = true });

            ctx.CourseAssignments.Add(new CourseAssignment
            {
                Id = assignmentId,
                CourseId = courseId,
                UserId = target.Id,
                AssignedDate = DateTime.UtcNow,
                IsActive = true
            });

            ctx.UserLocationMemberships.Add(new UserLocationMembership
            {
                UserId = target.Id,
                LocationId = location.Id,
                CreateDate = DateTime.UtcNow
            });

            await ctx.SaveChangesAsync();
        }

        Mock<ISecurityService> securityServiceMock = BuildSecurityServiceMock(isAdmin: true, admin);
        GdprService service = BuildService(options, userManager, securityServiceMock);

        Result<bool> result = await service.EraseUserDataAsync(target.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext verifyCtx = new(options);

        Assert.IsFalse(await verifyCtx.CourseAssignments.AnyAsync(x => x.Id == assignmentId));
        Assert.IsFalse(await verifyCtx.UserLocationMemberships.AnyAsync(x => x.UserId == target.Id));
    }

    [TestMethod]
    public async Task EraseUserDataAsync_ScrubsFileAndFolderAttributionStrings()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        UserManager<UserProfile> userManager = BuildUserManager(options);

        UserProfile admin = await SeedUserAsync(userManager, "Ada", "Min", "admin@example.com");
        UserProfile target = await SeedUserAsync(userManager);

        Guid fileId = Guid.NewGuid();
        Guid folderId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.FileMetadata.Add(new FileMetadata
            {
                Id = fileId,
                FileName = "test.mp3",
                FilePath = "/uploads/test.mp3",
                UploadedBy = target.Id
            });

            ctx.Folders.Add(new Folder
            {
                Id = folderId,
                Name = "Test Folder",
                CreatedBy = target.Id
            });

            await ctx.SaveChangesAsync();
        }

        Mock<ISecurityService> securityServiceMock = BuildSecurityServiceMock(isAdmin: true, admin);
        GdprService service = BuildService(options, userManager, securityServiceMock);

        Result<bool> result = await service.EraseUserDataAsync(target.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext verifyCtx = new(options);

        FileMetadata? file = await verifyCtx.FileMetadata.FindAsync(fileId);
        Assert.IsNotNull(file);
        Assert.IsNull(file.UploadedBy);

        Folder? folder = await verifyCtx.Folders.FindAsync(folderId);
        Assert.IsNotNull(folder);
        Assert.IsNull(folder.CreatedBy);
    }

    [TestMethod]
    public async Task EraseUserDataAsync_AlreadyErasedUser_FailsCleanly()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        UserManager<UserProfile> userManager = BuildUserManager(options);

        UserProfile admin = await SeedUserAsync(userManager, "Ada", "Min", "admin@example.com");
        UserProfile target = await SeedUserAsync(userManager);

        Mock<ISecurityService> securityServiceMock = BuildSecurityServiceMock(isAdmin: true, admin);
        GdprService service = BuildService(options, userManager, securityServiceMock);

        Result<bool> firstResult = await service.EraseUserDataAsync(target.Id);
        Assert.IsTrue(firstResult.IsSuccess, firstResult.Error);

        // Retrying against an already-erased user must fail cleanly (not throw) - the same
        // safety property that makes retrying after a genuine partial failure sound.
        Result<bool> secondResult = await service.EraseUserDataAsync(target.Id);
        Assert.IsFalse(secondResult.IsSuccess);
    }
}
