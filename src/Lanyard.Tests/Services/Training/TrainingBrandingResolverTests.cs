using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.Branding;
using Lanyard.Infrastructure.DTO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Training;

[TestClass]
public class TrainingBrandingResolverTests
{
    private static readonly Guid CompanyLogoId = Guid.NewGuid();

    private static TrainingBrandingResolver GetResolver(Mock<ICompanyLocationService> companyLocationServiceMock) =>
        new(companyLocationServiceMock.Object, NullLogger<TrainingBrandingResolver>.Instance);

    private static Result<CompanyBrandingInfo> Branding(int companyId, string hex) =>
        Result<CompanyBrandingInfo>.Ok(new CompanyBrandingInfo(companyId, hex, CompanyLogoId, null));

    [TestMethod]
    public async Task ResolveAsync_PrefersTheLearnersOwnCompany()
    {
        Mock<ICompanyLocationService> mock = new();
        mock.Setup(c => c.GetCompanyBrandingForUserAsync("user-1")).ReturnsAsync(Branding(7, "#abcdef"));

        TrainingBranding result = await GetResolver(mock).ResolveAsync("user-1", assignmentLocationId: 99, courseLocationId: 42);

        Assert.AreEqual("#abcdef", result.AccentColorHex);
        Assert.AreEqual(7, result.CompanyId);

        // The learner's company wins outright - location lookups shouldn't even be attempted.
        mock.Verify(c => c.GetCompanyBrandingForLocationAsync(It.IsAny<int>()), Times.Never());
    }

    // The regression this whole class exists for: two learners at one location used to get
    // different branding because their assignments were created by different routes, which
    // left different (or null) values in CourseAssignment.LocationId.
    [TestMethod]
    public async Task ResolveAsync_SameUserCompany_IsUnaffectedByDifferingAssignmentLocations()
    {
        Mock<ICompanyLocationService> mock = new();
        mock.Setup(c => c.GetCompanyBrandingForUserAsync(It.IsAny<string>())).ReturnsAsync(Branding(7, "#abcdef"));

        TrainingBrandingResolver resolver = GetResolver(mock);

        TrainingBranding adminAssigned = await resolver.ResolveAsync("user-1", assignmentLocationId: 1, courseLocationId: 1);
        TrainingBranding managerAssigned = await resolver.ResolveAsync("user-2", assignmentLocationId: 2, courseLocationId: 1);
        TrainingBranding autoAssigned = await resolver.ResolveAsync("user-3", assignmentLocationId: null, courseLocationId: null);

        Assert.AreEqual(adminAssigned, managerAssigned);
        Assert.AreEqual(adminAssigned, autoAssigned);
    }

    [TestMethod]
    public async Task ResolveAsync_NoUserCompany_FallsBackToAssignmentLocation()
    {
        Mock<ICompanyLocationService> mock = new();
        mock.Setup(c => c.GetCompanyBrandingForUserAsync(It.IsAny<string>()))
            .ReturnsAsync(Result<CompanyBrandingInfo>.Fail("no membership"));
        mock.Setup(c => c.GetCompanyBrandingForLocationAsync(5)).ReturnsAsync(Branding(3, "#112233"));

        TrainingBranding result = await GetResolver(mock).ResolveAsync("user-1", assignmentLocationId: 5, courseLocationId: 9);

        Assert.AreEqual("#112233", result.AccentColorHex);
        Assert.AreEqual(3, result.CompanyId);
    }

    [TestMethod]
    public async Task ResolveAsync_NoUserOrAssignmentLocation_FallsBackToCourseLocation()
    {
        Mock<ICompanyLocationService> mock = new();
        mock.Setup(c => c.GetCompanyBrandingForUserAsync(It.IsAny<string>()))
            .ReturnsAsync(Result<CompanyBrandingInfo>.Fail("no membership"));
        mock.Setup(c => c.GetCompanyBrandingForLocationAsync(9)).ReturnsAsync(Branding(4, "#445566"));

        TrainingBranding result = await GetResolver(mock).ResolveAsync("user-1", assignmentLocationId: null, courseLocationId: 9);

        Assert.AreEqual("#445566", result.AccentColorHex);
    }

    [TestMethod]
    public async Task ResolveAsync_NothingResolves_ReturnsDefaultLanyardBranding()
    {
        Mock<ICompanyLocationService> mock = new();
        mock.Setup(c => c.GetCompanyBrandingForUserAsync(It.IsAny<string>()))
            .ReturnsAsync(Result<CompanyBrandingInfo>.Fail("no membership"));
        mock.Setup(c => c.GetCompanyBrandingForLocationAsync(It.IsAny<int>()))
            .ReturnsAsync(Result<CompanyBrandingInfo>.Fail("not found"));

        TrainingBranding result = await GetResolver(mock).ResolveAsync("user-1", assignmentLocationId: 1, courseLocationId: 2);

        Assert.AreEqual(BrandConstants.PrimaryColorHex, result.AccentColorHex);
        Assert.IsNull(result.CompanyId);
        Assert.IsNull(result.LogoFileId);
    }

    [TestMethod]
    public async Task ResolveAsync_LookupThrows_ReturnsDefaultRatherThanPropagating()
    {
        Mock<ICompanyLocationService> mock = new();
        mock.Setup(c => c.GetCompanyBrandingForUserAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        TrainingBranding result = await GetResolver(mock).ResolveAsync("user-1", assignmentLocationId: 1, courseLocationId: 2);

        Assert.AreEqual(BrandConstants.PrimaryColorHex, result.AccentColorHex);
    }
}
