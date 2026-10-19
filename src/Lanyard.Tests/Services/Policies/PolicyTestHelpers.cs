using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Policies;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Lanyard.Tests.Services.Policies;

// Shared seeding for the policy service tests: one company with two locations (Amy at Ipswich,
// Bob at Wisbech) and a second company (Cara), plus a Kitchen position.
internal sealed class PolicyWorld
{
    public static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    public required DbContextOptions<ApplicationDbContext> Options { get; init; }
    public required Company Company { get; init; }
    public required Location Ipswich { get; init; }
    public required Location Wisbech { get; init; }
    public required Company OtherCompany { get; init; }
    public required Location OtherLocation { get; init; }
    public required UserProfile Amy { get; init; }
    public required UserProfile Bob { get; init; }
    public required UserProfile Cara { get; init; }
    public required StaffPosition Kitchen { get; init; }

    public TestClock Clock { get; } = new(Now);

    public Mock<IFileService> Files { get; } = new();

    // Every notification the policy services would have sent.
    public RecordingNotificationDispatcher Notifications { get; } = new();

    // What PolicyService asked the file service to upload, in order.
    public List<FileUploadOptions> UploadOptions { get; } = [];

    public static async Task<PolicyWorld> CreateAsync()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        (Company company, Location ipswich) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Play2Day", "Ipswich");
        (Company otherCompany, Location otherLocation) = await SchedulingTestHelpers.SeedCompanyAsync(options, "Other Co", "Elsewhere");

        Location wisbech;

        await using (ApplicationDbContext ctx = new(options))
        {
            wisbech = new Location { CompanyId = company.Id, Name = "Wisbech", IsActive = true };
            ctx.Locations.Add(wisbech);
            await ctx.SaveChangesAsync();
        }

        PolicyWorld world = new()
        {
            Options = options,
            Company = company,
            Ipswich = ipswich,
            Wisbech = wisbech,
            OtherCompany = otherCompany,
            OtherLocation = otherLocation,
            Amy = await SchedulingTestHelpers.SeedUserAsync(options, ipswich, "Amy"),
            Bob = await SchedulingTestHelpers.SeedUserAsync(options, wisbech, "Bob"),
            Cara = await SchedulingTestHelpers.SeedUserAsync(options, otherLocation, "Cara"),
            Kitchen = await SchedulingTestHelpers.SeedPositionAsync(options, company, "Kitchen")
        };

        world.Files
            .Setup(x => x.UploadFileAsync(It.IsAny<IFormFile>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<FileUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IFormFile file, Guid? _, string by, FileUploadOptions uploadOptions, CancellationToken _) =>
            {
                world.UploadOptions.Add(uploadOptions);
                return Result<FileMetadata>.Ok(new FileMetadata
                {
                    Id = Guid.NewGuid(),
                    CompanyId = company.Id,
                    FileName = file.FileName,
                    FilePath = "test",
                    UploadedBy = by,
                    IsRestricted = uploadOptions.IsRestricted
                });
            });

        world.Files
            .Setup(x => x.OpenRestrictedFileContentAsync(It.IsAny<Guid>(), It.IsAny<FileByteRange?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, FileByteRange? _, CancellationToken _) => Result<FileContent>.Ok(new FileContent
            {
                Stream = new MemoryStream(PdfBytes),
                Metadata = new FileMetadata { Id = id, FileName = "policy.pdf", FilePath = "test" },
                TotalLength = PdfBytes.Length
            }));

        return world;
    }

    public static readonly byte[] PdfBytes = "%PDF-1.7\nfire policy\n%%EOF"u8.ToArray();

    public static IFormFile Pdf(string fileName = "Fire policy.pdf", byte[]? bytes = null)
    {
        byte[] content = bytes ?? PdfBytes;
        return new FormFile(new MemoryStream(content), 0, content.Length, "file", fileName);
    }

    public LocationScope AdminScope => new(true, Ipswich.Id, Company.Id, "Ipswich");

    public LocationScope ManagerAt(Location location) => SchedulingTestHelpers.ManagerScopeFor(location);

    public LocationScope StaffAt(Location location) => SchedulingTestHelpers.StaffScopeFor(location);

    public ITenantContext Tenant(int? companyId = null, bool isSystem = false) => new TestTenant(companyId ?? Company.Id, isSystem);

    public PolicyAudienceService Audience() =>
        new(SchedulingTestHelpers.GetFactory(Options), Clock, NullLogger<PolicyAudienceService>.Instance);

    public PolicyReminderService Reminders(ITenantContext? tenant = null) =>
        new(SchedulingTestHelpers.GetFactory(Options), tenant ?? Tenant(), Notifications, Clock, NullLogger<PolicyReminderService>.Instance);

    public PolicyService Policies(ITenantContext? tenant = null) =>
        new(SchedulingTestHelpers.GetFactory(Options), tenant ?? Tenant(), Files.Object, Audience(), Reminders(tenant), Clock, NullLogger<PolicyService>.Instance);

    public PolicyAcknowledgementService Acknowledgements(ITenantContext? tenant = null) =>
        new(SchedulingTestHelpers.GetFactory(Options), tenant ?? Tenant(), Clock, NullLogger<PolicyAcknowledgementService>.Instance);

    // Creates a policy as an Admin and publishes its first version; returns the policy id.
    public async Task<Guid> PublishAsync(string name = "Fire policy", int? locationId = -1, IReadOnlyCollection<Guid>? positionIds = null,
        int acknowledgeWithinDays = 14, int? reacknowledgeEveryMonths = null)
    {
        PolicyService service = Policies();

        Result<Guid> saved = await service.SavePolicyAsync(
            new(null, name, null, locationId == -1 ? Ipswich.Id : locationId, positionIds ?? [], acknowledgeWithinDays, reacknowledgeEveryMonths),
            AdminScope, "admin");

        if (!saved.IsSuccess)
        {
            throw new InvalidOperationException(saved.Error);
        }

        Result<Infrastructure.DTO.Policies.PolicyVersionInfo> published = await service.PublishVersionAsync(saved.Data, Pdf(), "First issue", true, AdminScope, "admin");

        if (!published.IsSuccess)
        {
            throw new InvalidOperationException(published.Error);
        }

        return saved.Data;
    }

    public async Task<PolicyAssignment?> AssignmentAsync(Guid policyId, UserProfile user)
    {
        await using ApplicationDbContext ctx = new(Options);
        return await ctx.PolicyAssignments.AsNoTracking().SingleOrDefaultAsync(x => x.PolicyId == policyId && x.UserId == user.Id);
    }

    public async Task<Guid> CurrentVersionIdAsync(Guid policyId)
    {
        await using ApplicationDbContext ctx = new(Options);
        Policy policy = await ctx.Policies.AsNoTracking().SingleAsync(x => x.Id == policyId);
        return (await ctx.PolicyVersions.AsNoTracking().SingleAsync(x => x.PolicyId == policyId && x.VersionNumber == policy.CurrentVersionNumber)).Id;
    }

    private sealed class TestTenant(int? companyId, bool isSystem) : ITenantContext
    {
        public bool IsSystem => isSystem;
        public int? CompanyId => isSystem ? null : companyId;
        public bool IsPlatformAdmin => false;
        public int? ManageableCompanyId => isSystem ? null : companyId ?? -1;
        public bool CanManageCompany(int id) => ManageableCompanyId is null || ManageableCompanyId == id;
    }
}
