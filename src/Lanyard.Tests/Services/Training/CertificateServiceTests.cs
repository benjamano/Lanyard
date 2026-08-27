using System.Text;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Training;

[TestClass]
public class CertificateServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    private static CertificateService GetService(
        DbContextOptions<ApplicationDbContext> options,
        Mock<ICompanyLocationService>? companyLocationServiceMock = null,
        Mock<IFileService>? fileServiceMock = null)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new CertificateService(
            factoryMock.Object,
            (companyLocationServiceMock ?? new Mock<ICompanyLocationService>()).Object,
            (fileServiceMock ?? new Mock<IFileService>()).Object,
            NullLogger<CertificateService>.Instance);
    }

    private static async Task<(Course course, UserProfile user, CourseAssignment assignment)> SeedAsync(
        DbContextOptions<ApplicationDbContext> options,
        DateTime? completedDate,
        int? locationId = null,
        string userId = "user-1")
    {
        await using ApplicationDbContext ctx = new(options);

        Course course = new()
        {
            Id = Guid.NewGuid(),
            Name = "Fire Safety",
            PassMarkPercent = 80,
            IsActive = true
        };

        UserProfile user = new() { Id = userId, UserName = userId, FirstName = "Jane", LastName = "Doe" };

        CourseAssignment assignment = new()
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            UserId = userId,
            AssignedDate = DateTime.UtcNow.AddDays(-5),
            CompletedDate = completedDate,
            IsActive = true,
            LocationId = locationId
        };

        ctx.Courses.Add(course);
        ctx.Users.Add(user);
        ctx.CourseAssignments.Add(assignment);
        await ctx.SaveChangesAsync();

        return (course, user, assignment);
    }

    [TestMethod]
    public async Task GenerateCertificatePdfAsync_CompletedAssignment_ReturnsPdfBytes()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CertificateService service = GetService(options);
        (_, UserProfile user, CourseAssignment assignment) = await SeedAsync(options, DateTime.UtcNow);

        Result<byte[]> result = await service.GenerateCertificatePdfAsync(assignment.Id, user.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Data);
        Assert.IsTrue(result.Data!.Length > 0);

        // %PDF magic header - proves a real document came back, not just any byte array.
        Assert.AreEqual("%PDF", Encoding.ASCII.GetString(result.Data, 0, 4));
    }

    [TestMethod]
    public async Task GenerateCertificatePdfAsync_RequestedByAnotherUser_Fails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CertificateService service = GetService(options);
        (_, _, CourseAssignment assignment) = await SeedAsync(options, DateTime.UtcNow);

        Result<byte[]> result = await service.GenerateCertificatePdfAsync(assignment.Id, "someone-else");

        Assert.IsFalse(result.Success);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task GenerateCertificatePdfAsync_IncompleteAssignment_Fails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CertificateService service = GetService(options);
        (_, UserProfile user, CourseAssignment assignment) = await SeedAsync(options, completedDate: null);

        Result<byte[]> result = await service.GenerateCertificatePdfAsync(assignment.Id, user.Id);

        Assert.IsFalse(result.Success);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task GenerateCertificatePdfAsync_UnknownAssignment_Fails()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        CertificateService service = GetService(options);

        Result<byte[]> result = await service.GenerateCertificatePdfAsync(Guid.NewGuid(), "user-1");

        Assert.IsFalse(result.Success);
        Assert.IsNull(result.Data);
    }

    [TestMethod]
    public async Task GenerateCertificatePdfAsync_BrandingLookupThrows_StillProducesCertificate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Mock<ICompanyLocationService> companyLocationServiceMock = new();
        companyLocationServiceMock
            .Setup(c => c.GetCompanyBrandingForLocationAsync(It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("branding lookup exploded"));

        CertificateService service = GetService(options, companyLocationServiceMock);
        (_, UserProfile user, CourseAssignment assignment) = await SeedAsync(options, DateTime.UtcNow, locationId: 7);

        Result<byte[]> result = await service.GenerateCertificatePdfAsync(assignment.Id, user.Id);

        // Branding is decoration - losing it must not cost the learner their certificate.
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("%PDF", Encoding.ASCII.GetString(result.Data!, 0, 4));
    }

    [TestMethod]
    public async Task GenerateCertificatePdfAsync_LogoDownloadFails_StillProducesCertificate()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();

        Mock<ICompanyLocationService> companyLocationServiceMock = new();
        companyLocationServiceMock
            .Setup(c => c.GetCompanyBrandingForLocationAsync(It.IsAny<int>()))
            .ReturnsAsync(Result<CompanyBrandingInfo>.Ok(new CompanyBrandingInfo(1, "#123456", Guid.NewGuid(), null)));

        Mock<IFileService> fileServiceMock = new();
        fileServiceMock
            .Setup(f => f.DownloadFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Stream>.Fail("file missing"));

        CertificateService service = GetService(options, companyLocationServiceMock, fileServiceMock);
        (_, UserProfile user, CourseAssignment assignment) = await SeedAsync(options, DateTime.UtcNow, locationId: 7);

        Result<byte[]> result = await service.GenerateCertificatePdfAsync(assignment.Id, user.Id);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("%PDF", Encoding.ASCII.GetString(result.Data!, 0, 4));
    }
}
