#nullable enable

using Lanyard.Application.Services.Announcements;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Announcements;

[TestClass]
public class AnnouncementServiceTests
{
    private const int HomeLocationId = 1;
    private const int OtherLocationId = 2;

    private static readonly LocationScope StaffScope = new(false, HomeLocationId, 1, "Home");
    private static readonly LocationScope OtherStaffScope = new(false, OtherLocationId, 1, "Other");
    private static readonly LocationScope AdminScope = new(true, HomeLocationId, 1, "Home");

    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static AnnouncementService GetService(DbContextOptions<ApplicationDbContext> options)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new AnnouncementService(factoryMock.Object, NullLogger<AnnouncementService>.Instance);
    }

    private static async Task SeedAsync(DbContextOptions<ApplicationDbContext> options, params Announcement[] announcements)
    {
        await using ApplicationDbContext ctx = new(options);

        ctx.Announcements.AddRange(announcements);
        await ctx.SaveChangesAsync();
    }

    private static Announcement Make(
        string title,
        int? locationId = HomeLocationId,
        bool isPinned = false,
        bool isActive = true,
        DateTime? expiryDate = null,
        DateTime? createDate = null)
    {
        return new Announcement
        {
            Id = Guid.NewGuid(),
            Title = title,
            Body = $"Body of {title}",
            IsPinned = isPinned,
            LocationId = locationId,
            ExpiryDate = expiryDate,
            IsActive = isActive,
            CreateDate = createDate ?? DateTime.UtcNow
        };
    }

    [TestMethod]
    public async Task GetActiveAnnouncements_ExcludesExpired()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await SeedAsync(options,
            Make("Live", expiryDate: DateTime.UtcNow.AddDays(1)),
            Make("Expired", expiryDate: DateTime.UtcNow.AddDays(-1)),
            Make("Never expires"));

        AnnouncementService service = GetService(options);

        Result<List<Announcement>> result = await service.GetActiveAnnouncementsAsync(StaffScope, 10);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEquivalent(
            new[] { "Live", "Never expires" },
            result.Data!.Select(x => x.Title).ToList());
    }

    [TestMethod]
    public async Task GetActiveAnnouncements_SortsPinnedFirstThenNewest()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        DateTime now = DateTime.UtcNow;

        await SeedAsync(options,
            Make("Newest unpinned", createDate: now),
            Make("Older unpinned", createDate: now.AddDays(-2)),
            Make("Pinned but old", isPinned: true, createDate: now.AddDays(-10)));

        AnnouncementService service = GetService(options);

        Result<List<Announcement>> result = await service.GetActiveAnnouncementsAsync(StaffScope, 10);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(
            new[] { "Pinned but old", "Newest unpinned", "Older unpinned" },
            result.Data!.Select(x => x.Title).ToList());
    }

    [TestMethod]
    public async Task GetActiveAnnouncements_ExcludesOtherLocationsAndSoftDeleted()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await SeedAsync(options,
            Make("Mine"),
            Make("Another site", locationId: OtherLocationId),
            Make("Unassigned", locationId: null),
            Make("Deleted", isActive: false));

        AnnouncementService service = GetService(options);

        Result<List<Announcement>> result = await service.GetActiveAnnouncementsAsync(StaffScope, 10);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.Data!.Count);
        Assert.AreEqual("Mine", result.Data![0].Title);
    }

    [TestMethod]
    public async Task GetActiveAnnouncements_RespectsMaxItems()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await SeedAsync(options, Make("One"), Make("Two"), Make("Three"), Make("Four"));

        AnnouncementService service = GetService(options);

        Result<List<Announcement>> result = await service.GetActiveAnnouncementsAsync(StaffScope, 2);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, result.Data!.Count);
    }

    [TestMethod]
    public async Task GetActiveAnnouncements_WithNoLocationReturnsNothing()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await SeedAsync(options, Make("Mine"), Make("Another site", locationId: OtherLocationId));

        AnnouncementService service = GetService(options);

        // The anonymous kiosk case: no location must mean no announcements, never every site's.
        Result<List<Announcement>> result =
            await service.GetActiveAnnouncementsAsync(new LocationScope(false, null, null, null), 10);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(0, result.Data!.Count);
    }

    [TestMethod]
    public async Task GetAnnouncements_IncludesExpiredSoManagersCanTidyUp()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await SeedAsync(options,
            Make("Live"),
            Make("Expired", expiryDate: DateTime.UtcNow.AddDays(-1)));

        AnnouncementService service = GetService(options);

        Result<List<Announcement>> result = await service.GetAnnouncementsAsync(StaffScope, false);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, result.Data!.Count);
    }

    [TestMethod]
    public async Task GetAnnouncements_AdminAllLocationsLiftsTheLocationFilter()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await SeedAsync(options, Make("Mine"), Make("Another site", locationId: OtherLocationId));

        AnnouncementService service = GetService(options);

        Result<List<Announcement>> scoped = await service.GetAnnouncementsAsync(AdminScope, false);
        Assert.IsTrue(scoped.IsSuccess, scoped.Error);
        Assert.AreEqual(1, scoped.Data!.Count);

        Result<List<Announcement>> all = await service.GetAnnouncementsAsync(AdminScope, true);
        Assert.IsTrue(all.IsSuccess, all.Error);
        Assert.AreEqual(2, all.Data!.Count);
    }

    [TestMethod]
    public async Task GetAnnouncements_NonAdminCannotSeeOtherLocationsEvenWithAllLocations()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        await SeedAsync(options, Make("Mine"), Make("Another site", locationId: OtherLocationId));

        AnnouncementService service = GetService(options);

        Result<List<Announcement>> result = await service.GetAnnouncementsAsync(StaffScope, true);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.Data!.Count);
        Assert.AreEqual("Mine", result.Data![0].Title);
    }

    [TestMethod]
    public async Task SaveAnnouncement_CreatesWithPostersOwnLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        AnnouncementService service = GetService(options);

        // A non-admin naming someone else's location gets pinned to their own regardless.
        Announcement announcement = new()
        {
            Title = "  Fire drill  ",
            Body = "  Thursday at 10am.  ",
            LocationId = OtherLocationId
        };

        Result<Announcement> result = await service.SaveAnnouncementAsync(announcement, StaffScope);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Announcement saved = await ctx.Announcements.SingleAsync();

        Assert.AreEqual("Fire drill", saved.Title);
        Assert.AreEqual("Thursday at 10am.", saved.Body);
        Assert.AreEqual(HomeLocationId, saved.LocationId);
        Assert.IsTrue(saved.IsActive);
        Assert.AreNotEqual(Guid.Empty, saved.Id);
    }

    [TestMethod]
    public async Task SaveAnnouncement_AdminCanPostToAnotherLocation()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        AnnouncementService service = GetService(options);

        Announcement announcement = new()
        {
            Title = "Head office notice",
            Body = "Applies to the other site.",
            LocationId = OtherLocationId
        };

        Result<Announcement> result = await service.SaveAnnouncementAsync(announcement, AdminScope);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Announcement saved = await ctx.Announcements.SingleAsync();

        Assert.AreEqual(OtherLocationId, saved.LocationId);
    }

    [TestMethod]
    public async Task SaveAnnouncement_RejectsEditOfAnotherLocationsAnnouncement()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Announcement existing = Make("Another site", locationId: OtherLocationId);
        await SeedAsync(options, existing);

        AnnouncementService service = GetService(options);

        Result<Announcement> result = await service.SaveAnnouncementAsync(
            new Announcement
            {
                Id = existing.Id,
                Title = "Hijacked",
                Body = "Should not save.",
                LocationId = OtherLocationId
            },
            StaffScope);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("You do not have access to this announcement.", result.Error);
    }

    [TestMethod]
    public async Task SaveAnnouncement_RejectsMissingTitleOrBody()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        AnnouncementService service = GetService(options);

        Result<Announcement> noTitle = await service.SaveAnnouncementAsync(
            new Announcement { Title = "   ", Body = "Body" }, StaffScope);

        Result<Announcement> noBody = await service.SaveAnnouncementAsync(
            new Announcement { Title = "Title", Body = "   " }, StaffScope);

        Assert.IsFalse(noTitle.IsSuccess);
        Assert.IsFalse(noBody.IsSuccess);
    }

    [TestMethod]
    public async Task SaveAnnouncement_RejectsWhenNoLocationCanBeResolved()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        AnnouncementService service = GetService(options);

        Result<Announcement> result = await service.SaveAnnouncementAsync(
            new Announcement { Title = "Orphan", Body = "No location." },
            new LocationScope(false, null, null, null));

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public async Task SaveAnnouncement_UpdatesExistingInPlace()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Announcement existing = Make("Original");
        await SeedAsync(options, existing);

        AnnouncementService service = GetService(options);

        Result<Announcement> result = await service.SaveAnnouncementAsync(
            new Announcement
            {
                Id = existing.Id,
                Title = "Updated",
                Body = "New body.",
                IsPinned = true,
                LocationId = HomeLocationId
            },
            StaffScope);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Announcement saved = await ctx.Announcements.SingleAsync();

        Assert.AreEqual("Updated", saved.Title);
        Assert.IsTrue(saved.IsPinned);
        Assert.IsNotNull(saved.LastUpdateDate);
    }

    [TestMethod]
    public async Task DeleteAnnouncement_SoftDeletesRatherThanRemovingTheRow()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Announcement existing = Make("Doomed");
        await SeedAsync(options, existing);

        AnnouncementService service = GetService(options);

        Result<bool> result = await service.DeleteAnnouncementAsync(existing.Id, StaffScope);

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        Announcement saved = await ctx.Announcements.SingleAsync();

        Assert.IsFalse(saved.IsActive);
    }

    [TestMethod]
    public async Task DeleteAnnouncement_RejectsAnotherLocationsAnnouncement()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Announcement existing = Make("Another site", locationId: OtherLocationId);
        await SeedAsync(options, existing);

        AnnouncementService service = GetService(options);

        Result<bool> result = await service.DeleteAnnouncementAsync(existing.Id, OtherStaffScope);
        Assert.IsTrue(result.IsSuccess, result.Error);

        Announcement second = Make("Mine again", locationId: OtherLocationId);
        await SeedAsync(options, second);

        Result<bool> rejected = await service.DeleteAnnouncementAsync(second.Id, StaffScope);

        Assert.IsFalse(rejected.IsSuccess);
        Assert.AreEqual("You do not have access to this announcement.", rejected.Error);
    }

    [TestMethod]
    public async Task DeleteAnnouncement_FailsWhenNotFound()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        AnnouncementService service = GetService(options);

        Result<bool> result = await service.DeleteAnnouncementAsync(Guid.NewGuid(), StaffScope);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Announcement not found.", result.Error);
    }
}
