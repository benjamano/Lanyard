using Lanyard.Application.Services;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Onboarding;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Onboarding;

[TestClass]
public class OnboardingServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static OnboardingService GetService(
        DbContextOptions<ApplicationDbContext> options,
        Mock<IFileService>? fileServiceMock = null,
        Mock<IEmailService>? emailServiceMock = null,
        Mock<ITrainingBrandingResolver>? brandingResolverMock = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new OnboardingService(
            factoryMock.Object,
            (fileServiceMock ?? new Mock<IFileService>()).Object,
            (emailServiceMock ?? new Mock<IEmailService>()).Object,
            (brandingResolverMock ?? new Mock<ITrainingBrandingResolver>()).Object,
            Options.Create(new EmailOptions { PublicBaseUrl = "https://lanyard.example.com" }),
            NullLogger<OnboardingService>.Instance);
    }

    private static async Task<Company> SeedCompanyAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = "Play2Day", IsActive = true };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        return company;
    }

    private static async Task<Location> SeedLocationAsync(DbContextOptions<ApplicationDbContext> options, int companyId)
    {
        await using ApplicationDbContext ctx = new(options);

        Location location = new() { Name = "Ipswich", CompanyId = companyId, IsActive = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        return location;
    }

    [TestMethod]
    public async Task GetSettingsAsync_NoneConfigured_ReturnsOkWithNull()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);

        OnboardingService service = GetService(options);
        Result<CompanyOnboardingSettings?> result = await service.GetSettingsAsync(company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_NoExistingRow_CreatesOne()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);

        OnboardingService service = GetService(options);
        Result<CompanyOnboardingSettings> result = await service.SaveSettingsAsync(new CompanyOnboardingSettings
        {
            CompanyId = company.Id,
            SendWelcomeEmail = true,
            WelcomeEmailSubject = "Welcome!",
            WelcomeEmailBodyHtml = "<p>Hi</p>",
            AutoAttachStandingDocuments = true
        });

        Assert.IsTrue(result.IsSuccess, result.Error);

        await using ApplicationDbContext ctx = new(options);
        CompanyOnboardingSettings saved = await ctx.CompanyOnboardingSettings.SingleAsync(x => x.CompanyId == company.Id);
        Assert.IsTrue(saved.SendWelcomeEmail);
        Assert.AreEqual("Welcome!", saved.WelcomeEmailSubject);
        Assert.IsNull(saved.LocationId);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_ExistingRow_UpdatesInPlaceRatherThanDuplicating()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);

        OnboardingService service = GetService(options);
        await service.SaveSettingsAsync(new CompanyOnboardingSettings { CompanyId = company.Id, SendWelcomeEmail = false });
        await service.SaveSettingsAsync(new CompanyOnboardingSettings { CompanyId = company.Id, SendWelcomeEmail = true });

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(1, await ctx.CompanyOnboardingSettings.CountAsync(x => x.CompanyId == company.Id));
        Assert.IsTrue((await ctx.CompanyOnboardingSettings.SingleAsync(x => x.CompanyId == company.Id)).SendWelcomeEmail);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_CompanyWideAndLocationRow_CoexistAsSeparateRows()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        Location location = await SeedLocationAsync(options, company.Id);

        OnboardingService service = GetService(options);
        await service.SaveSettingsAsync(new CompanyOnboardingSettings { CompanyId = company.Id, LocationId = null, SendWelcomeEmail = false });
        await service.SaveSettingsAsync(new CompanyOnboardingSettings { CompanyId = company.Id, LocationId = location.Id, SendWelcomeEmail = true });

        await using ApplicationDbContext ctx = new(options);
        Assert.AreEqual(2, await ctx.CompanyOnboardingSettings.CountAsync(x => x.CompanyId == company.Id));

        Result<CompanyOnboardingSettings?> companyWide = await service.GetSettingsAsync(company.Id);
        Result<CompanyOnboardingSettings?> locationSpecific = await service.GetSettingsAsync(company.Id, location.Id);

        Assert.IsFalse(companyWide.Data!.SendWelcomeEmail);
        Assert.IsTrue(locationSpecific.Data!.SendWelcomeEmail);
    }

    [TestMethod]
    public async Task AddStandingAttachmentAsync_ThenGetStandingAttachmentsAsync_ReturnsItOrderedBySortOrder()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        Guid firstFileId = Guid.NewGuid();
        Guid secondFileId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.FileMetadata.AddRange(
                new FileMetadata { Id = firstFileId, FileName = "Handbook.pdf", FilePath = "/handbook.pdf" },
                new FileMetadata { Id = secondFileId, FileName = "Poster.pdf", FilePath = "/poster.pdf" });
            await ctx.SaveChangesAsync();
        }

        OnboardingService service = GetService(options);
        await service.AddStandingAttachmentAsync(company.Id, null, firstFileId);
        await service.AddStandingAttachmentAsync(company.Id, null, secondFileId);

        Result<List<CompanyOnboardingStandingAttachment>> result = await service.GetStandingAttachmentsAsync(company.Id);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.Data!.Count);
        Assert.AreEqual("Handbook.pdf", result.Data[0].FileMetadata!.FileName);
        Assert.AreEqual("Poster.pdf", result.Data[1].FileMetadata!.FileName);
    }

    [TestMethod]
    public async Task AddStandingAttachmentAsync_CompanyWideAndLocationScoped_AreKeptSeparate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        Location location = await SeedLocationAsync(options, company.Id);
        Guid companyFileId = Guid.NewGuid();
        Guid locationFileId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.FileMetadata.AddRange(
                new FileMetadata { Id = companyFileId, FileName = "CompanyHandbook.pdf", FilePath = "/a.pdf" },
                new FileMetadata { Id = locationFileId, FileName = "LocationPoster.pdf", FilePath = "/b.pdf" });
            await ctx.SaveChangesAsync();
        }

        OnboardingService service = GetService(options);
        await service.AddStandingAttachmentAsync(company.Id, null, companyFileId);
        await service.AddStandingAttachmentAsync(company.Id, location.Id, locationFileId);

        Result<List<CompanyOnboardingStandingAttachment>> companyWide = await service.GetStandingAttachmentsAsync(company.Id);
        Result<List<CompanyOnboardingStandingAttachment>> locationScoped = await service.GetStandingAttachmentsAsync(company.Id, location.Id);

        Assert.AreEqual(1, companyWide.Data!.Count);
        Assert.AreEqual("CompanyHandbook.pdf", companyWide.Data[0].FileMetadata!.FileName);
        Assert.AreEqual(1, locationScoped.Data!.Count);
        Assert.AreEqual("LocationPoster.pdf", locationScoped.Data[0].FileMetadata!.FileName);
    }

    [TestMethod]
    public async Task RemoveStandingAttachmentAsync_SoftDeletes_NotHardDeletes()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        Guid fileId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.FileMetadata.Add(new FileMetadata { Id = fileId, FileName = "Handbook.pdf", FilePath = "/handbook.pdf" });
            await ctx.SaveChangesAsync();
        }

        OnboardingService service = GetService(options);
        Result<CompanyOnboardingStandingAttachment> added = await service.AddStandingAttachmentAsync(company.Id, null, fileId);

        Result<bool> result = await service.RemoveStandingAttachmentAsync(added.Data!.Id);
        Assert.IsTrue(result.IsSuccess);

        await using ApplicationDbContext verifyCtx = new(options);
        CompanyOnboardingStandingAttachment row = await verifyCtx.CompanyOnboardingStandingAttachments.SingleAsync(x => x.Id == added.Data!.Id);
        Assert.IsFalse(row.IsActive);

        Result<List<CompanyOnboardingStandingAttachment>> activeAttachments = await service.GetStandingAttachmentsAsync(company.Id);
        Assert.AreEqual(0, activeAttachments.Data!.Count);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_NoResolvableCompany_NoOpsWithoutSendingEmail()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(It.IsAny<string>(), It.IsAny<int?>(), null))
            .ReturnsAsync(TrainingBranding.Default);

        Mock<IEmailService> emailServiceMock = new();

        OnboardingService service = GetService(options, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        Result<bool> result = await service.TriggerOnboardingAsync("user-1", null, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Data);
        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()),
            Times.Never);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_SettingsMissing_NoOpsWithoutSendingEmail()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        string userId;

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, It.IsAny<int?>(), null))
            .ReturnsAsync(new TrainingBranding("#000000", company.Id, null));

        Mock<IEmailService> emailServiceMock = new();

        OnboardingService service = GetService(options, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        Result<bool> result = await service.TriggerOnboardingAsync(userId, null, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Data);
        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()),
            Times.Never);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_WelcomeEmailDisabled_NoOpsWithoutSendingEmail()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        string userId;

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            ctx.CompanyOnboardingSettings.Add(new CompanyOnboardingSettings { CompanyId = company.Id, SendWelcomeEmail = false });
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, It.IsAny<int?>(), null))
            .ReturnsAsync(new TrainingBranding("#000000", company.Id, null));

        Mock<IEmailService> emailServiceMock = new();

        OnboardingService service = GetService(options, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        Result<bool> result = await service.TriggerOnboardingAsync(userId, null, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Data);
        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()),
            Times.Never);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_EnabledWithNoStandingAttachments_SendsEmailWithNoAttachments()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        string userId;

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            ctx.CompanyOnboardingSettings.Add(new CompanyOnboardingSettings
            {
                CompanyId = company.Id,
                SendWelcomeEmail = true,
                WelcomeEmailSubject = "Welcome!",
                WelcomeEmailBodyHtml = "<p>Hi</p>",
                AutoAttachStandingDocuments = false
            });
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, It.IsAny<int?>(), null))
            .ReturnsAsync(new TrainingBranding("#123456", company.Id, null));

        Mock<IEmailService> emailServiceMock = new();
        emailServiceMock.Setup(e => e.SendOnboardingWelcomeEmailAsync(
                It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        OnboardingService service = GetService(options, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        Result<bool> result = await service.TriggerOnboardingAsync(userId, null, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Data);
        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.Is<UserProfile>(u => u.Id == userId), "Welcome!", "<p>Hi</p>", null, "#123456",
            It.Is<IReadOnlyList<EmailAttachment>>(a => a.Count == 0)),
            Times.Once);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_EnabledWithStandingAttachments_BuffersAndSendsThem()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        string userId;
        Guid fileId = Guid.NewGuid();
        byte[] handbookBytes = [1, 2, 3];

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            ctx.FileMetadata.Add(new FileMetadata { Id = fileId, FileName = "Handbook.pdf", FilePath = "/handbook.pdf" });
            ctx.CompanyOnboardingSettings.Add(new CompanyOnboardingSettings
            {
                CompanyId = company.Id,
                SendWelcomeEmail = true,
                WelcomeEmailSubject = "Welcome!",
                WelcomeEmailBodyHtml = "<p>Hi</p>",
                AutoAttachStandingDocuments = true
            });
            ctx.CompanyOnboardingStandingAttachments.Add(new CompanyOnboardingStandingAttachment
            {
                CompanyId = company.Id,
                FileMetadataId = fileId,
                SortOrder = 0,
                IsActive = true
            });
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, It.IsAny<int?>(), null))
            .ReturnsAsync(new TrainingBranding("#123456", company.Id, null));

        Mock<IFileService> fileServiceMock = new();
        fileServiceMock.Setup(f => f.DownloadFileAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Stream>.Ok(new MemoryStream(handbookBytes)));

        Mock<IEmailService> emailServiceMock = new();
        emailServiceMock.Setup(e => e.SendOnboardingWelcomeEmailAsync(
                It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        OnboardingService service = GetService(options, fileServiceMock: fileServiceMock, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        Result<bool> result = await service.TriggerOnboardingAsync(userId, null, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error);
        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), "Welcome!", "<p>Hi</p>", null, "#123456",
            It.Is<IReadOnlyList<EmailAttachment>>(a => a.Count == 1 && a[0].FileName == "Handbook.pdf" && a[0].Content.SequenceEqual(handbookBytes))),
            Times.Once);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_StandingAttachmentFailsToDownload_DropsItButStillSendsEmail()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        string userId;
        Guid fileId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            ctx.FileMetadata.Add(new FileMetadata { Id = fileId, FileName = "Handbook.pdf", FilePath = "/handbook.pdf" });
            ctx.CompanyOnboardingSettings.Add(new CompanyOnboardingSettings
            {
                CompanyId = company.Id,
                SendWelcomeEmail = true,
                AutoAttachStandingDocuments = true
            });
            ctx.CompanyOnboardingStandingAttachments.Add(new CompanyOnboardingStandingAttachment
            {
                CompanyId = company.Id,
                FileMetadataId = fileId,
                SortOrder = 0,
                IsActive = true
            });
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, It.IsAny<int?>(), null))
            .ReturnsAsync(new TrainingBranding("#123456", company.Id, null));

        Mock<IFileService> fileServiceMock = new();
        fileServiceMock.Setup(f => f.DownloadFileAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Stream>.Fail("File not found in storage."));

        Mock<IEmailService> emailServiceMock = new();
        emailServiceMock.Setup(e => e.SendOnboardingWelcomeEmailAsync(
                It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        OnboardingService service = GetService(options, fileServiceMock: fileServiceMock, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        Result<bool> result = await service.TriggerOnboardingAsync(userId, null, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error);
        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.Is<IReadOnlyList<EmailAttachment>>(a => a.Count == 0)),
            Times.Once);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_StandingAttachmentOverSizeLimit_SkipsItWithoutDownloading()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        string userId;
        Guid fileId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            ctx.FileMetadata.Add(new FileMetadata { Id = fileId, FileName = "HugeHandbook.pdf", FilePath = "/huge.pdf", FileSize = 100 * 1024 * 1024 });
            ctx.CompanyOnboardingSettings.Add(new CompanyOnboardingSettings
            {
                CompanyId = company.Id,
                SendWelcomeEmail = true,
                AutoAttachStandingDocuments = true
            });
            ctx.CompanyOnboardingStandingAttachments.Add(new CompanyOnboardingStandingAttachment
            {
                CompanyId = company.Id,
                FileMetadataId = fileId,
                SortOrder = 0,
                IsActive = true
            });
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, It.IsAny<int?>(), null))
            .ReturnsAsync(new TrainingBranding("#123456", company.Id, null));

        Mock<IFileService> fileServiceMock = new();

        Mock<IEmailService> emailServiceMock = new();
        emailServiceMock.Setup(e => e.SendOnboardingWelcomeEmailAsync(
                It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        OnboardingService service = GetService(options, fileServiceMock: fileServiceMock, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        Result<bool> result = await service.TriggerOnboardingAsync(userId, null, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error);
        fileServiceMock.Verify(f => f.DownloadFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
            It.Is<IReadOnlyList<EmailAttachment>>(a => a.Count == 0)),
            Times.Once);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_HasLogo_BuildsLogoUrlFromCompanyIdAndFileId()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        string userId;
        Guid logoFileId = Guid.NewGuid();

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            ctx.CompanyOnboardingSettings.Add(new CompanyOnboardingSettings { CompanyId = company.Id, SendWelcomeEmail = true });
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, It.IsAny<int?>(), null))
            .ReturnsAsync(new TrainingBranding("#123456", company.Id, logoFileId));

        Mock<IEmailService> emailServiceMock = new();
        emailServiceMock.Setup(e => e.SendOnboardingWelcomeEmailAsync(
                It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        OnboardingService service = GetService(options, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        await service.TriggerOnboardingAsync(userId, null, CancellationToken.None);

        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(),
            $"https://lanyard.example.com/api/companies/{company.Id}/logo?v={logoFileId:N}",
            It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()),
            Times.Once);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_LocationOverrideExists_UsesLocationSettingsInsteadOfCompanyWide()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        Location location = await SeedLocationAsync(options, company.Id);
        string userId;

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            ctx.CompanyOnboardingSettings.AddRange(
                new CompanyOnboardingSettings { CompanyId = company.Id, LocationId = null, SendWelcomeEmail = true, WelcomeEmailSubject = "Company-wide" },
                new CompanyOnboardingSettings { CompanyId = company.Id, LocationId = location.Id, SendWelcomeEmail = true, WelcomeEmailSubject = "Location-specific" });
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, location.Id, null))
            .ReturnsAsync(new TrainingBranding("#123456", company.Id, null));

        Mock<IEmailService> emailServiceMock = new();
        emailServiceMock.Setup(e => e.SendOnboardingWelcomeEmailAsync(
                It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        OnboardingService service = GetService(options, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        await service.TriggerOnboardingAsync(userId, location.Id, CancellationToken.None);

        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), "Location-specific", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()),
            Times.Once);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_NoLocationOverride_FallsBackToCompanyWideSettings()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        Location location = await SeedLocationAsync(options, company.Id);
        string userId;

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            ctx.CompanyOnboardingSettings.Add(
                new CompanyOnboardingSettings { CompanyId = company.Id, LocationId = null, SendWelcomeEmail = true, WelcomeEmailSubject = "Company-wide" });
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, location.Id, null))
            .ReturnsAsync(new TrainingBranding("#123456", company.Id, null));

        Mock<IEmailService> emailServiceMock = new();
        emailServiceMock.Setup(e => e.SendOnboardingWelcomeEmailAsync(
                It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        OnboardingService service = GetService(options, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        await service.TriggerOnboardingAsync(userId, location.Id, CancellationToken.None);

        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), "Company-wide", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()),
            Times.Once);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_LocationOverrideDisabled_DoesNotFallBackToEnabledCompanyWideSettings()
    {
        // A location that has explicitly opted out (its own row with SendWelcomeEmail = false)
        // should stay opted out, even if the company default is enabled - an override replaces
        // the company-wide configuration entirely rather than only "adding" a location layer.
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        Location location = await SeedLocationAsync(options, company.Id);
        string userId;

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            ctx.CompanyOnboardingSettings.AddRange(
                new CompanyOnboardingSettings { CompanyId = company.Id, LocationId = null, SendWelcomeEmail = true },
                new CompanyOnboardingSettings { CompanyId = company.Id, LocationId = location.Id, SendWelcomeEmail = false });
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, location.Id, null))
            .ReturnsAsync(new TrainingBranding("#123456", company.Id, null));

        Mock<IEmailService> emailServiceMock = new();

        OnboardingService service = GetService(options, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        Result<bool> result = await service.TriggerOnboardingAsync(userId, location.Id, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Data);
        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()),
            Times.Never);
    }

    [TestMethod]
    public async Task GetEffectiveSettingsAsync_LocationOverrideExists_ReturnsLocationSettings()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        Location location = await SeedLocationAsync(options, company.Id);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CompanyOnboardingSettings.AddRange(
                new CompanyOnboardingSettings { CompanyId = company.Id, LocationId = null, SendWelcomeEmail = true, WelcomeEmailSubject = "Company-wide" },
                new CompanyOnboardingSettings { CompanyId = company.Id, LocationId = location.Id, SendWelcomeEmail = true, WelcomeEmailSubject = "Location-specific" });
            await ctx.SaveChangesAsync();
        }

        OnboardingService service = GetService(options);
        Result<CompanyOnboardingSettings?> result = await service.GetEffectiveSettingsAsync(company.Id, location.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Location-specific", result.Data?.WelcomeEmailSubject);
    }

    [TestMethod]
    public async Task GetEffectiveSettingsAsync_NoLocationOverride_FallsBackToCompanyWideSettings()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        Location location = await SeedLocationAsync(options, company.Id);

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.CompanyOnboardingSettings.Add(
                new CompanyOnboardingSettings { CompanyId = company.Id, LocationId = null, SendWelcomeEmail = true, WelcomeEmailSubject = "Company-wide" });
            await ctx.SaveChangesAsync();
        }

        OnboardingService service = GetService(options);
        Result<CompanyOnboardingSettings?> result = await service.GetEffectiveSettingsAsync(company.Id, location.Id);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("Company-wide", result.Data?.WelcomeEmailSubject);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_SubjectAndBodyOverridesProvided_SendsOverridesInsteadOfSettings()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        string userId;

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            ctx.CompanyOnboardingSettings.Add(new CompanyOnboardingSettings
            {
                CompanyId = company.Id,
                SendWelcomeEmail = true,
                WelcomeEmailSubject = "Default subject",
                WelcomeEmailBodyHtml = "<p>Default body</p>"
            });
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, It.IsAny<int?>(), null))
            .ReturnsAsync(new TrainingBranding("#123456", company.Id, null));

        Mock<IEmailService> emailServiceMock = new();
        emailServiceMock.Setup(e => e.SendOnboardingWelcomeEmailAsync(
                It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        OnboardingService service = GetService(options, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);
        await service.TriggerOnboardingAsync(userId, null, CancellationToken.None, "Custom subject", "<p>Custom body</p>");

        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), "Custom subject", "<p>Custom body</p>", It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()),
            Times.Once);
    }

    [TestMethod]
    public async Task TriggerOnboardingAsync_SubjectOverrideEmpty_FallsBackToSettingsSubject()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        Company company = await SeedCompanyAsync(options);
        string userId;

        await using (ApplicationDbContext ctx = new(options))
        {
            UserProfile user = new() { UserName = "jdoe", Email = "jane@example.com" };
            ctx.Users.Add(user);
            ctx.CompanyOnboardingSettings.Add(new CompanyOnboardingSettings
            {
                CompanyId = company.Id,
                SendWelcomeEmail = true,
                WelcomeEmailSubject = "Default subject",
                WelcomeEmailBodyHtml = "<p>Default body</p>"
            });
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }

        Mock<ITrainingBrandingResolver> brandingResolverMock = new();
        brandingResolverMock.Setup(b => b.ResolveAsync(userId, It.IsAny<int?>(), null))
            .ReturnsAsync(new TrainingBranding("#123456", company.Id, null));

        Mock<IEmailService> emailServiceMock = new();
        emailServiceMock.Setup(e => e.SendOnboardingWelcomeEmailAsync(
                It.IsAny<UserProfile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()))
            .ReturnsAsync(Result<bool>.Ok(true));

        OnboardingService service = GetService(options, emailServiceMock: emailServiceMock, brandingResolverMock: brandingResolverMock);

        // An empty subjectOverride (e.g. the admin clears the prefilled field) must fall back to
        // the location/company default rather than sending a blank subject line.
        await service.TriggerOnboardingAsync(userId, null, CancellationToken.None, string.Empty, null);

        emailServiceMock.Verify(e => e.SendOnboardingWelcomeEmailAsync(
            It.IsAny<UserProfile>(), "Default subject", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>>()),
            Times.Once);
    }
}
