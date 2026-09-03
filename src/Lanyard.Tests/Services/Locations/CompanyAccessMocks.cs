using Lanyard.Application.Services.Locations;
using Moq;

namespace Lanyard.Tests.Services.Locations;

/// <summary>
/// Stand-ins for ICompanyAccessService, so a test can say plainly whose permissions it is
/// running as.
///
/// Tests that are not about authorisation use <see cref="Admin"/> and go on testing what they
/// were testing before the company services started checking permissions.
/// </summary>
public static class CompanyAccessMocks
{
    /// <summary>Full rights over every company.</summary>
    public static ICompanyAccessService Admin() => Build(new CompanyAccess(true, []));

    /// <summary>A manager who may administer only the given companies.</summary>
    public static ICompanyAccessService ManagerOf(params int[] companyIds) =>
        Build(new CompanyAccess(false, companyIds));

    /// <summary>Signed out, or a role with no company rights at all.</summary>
    public static ICompanyAccessService None() => Build(CompanyAccess.None);

    private static ICompanyAccessService Build(CompanyAccess access)
    {
        Mock<ICompanyAccessService> mock = new();

        mock.Setup(a => a.GetCurrentAsync()).ReturnsAsync(access);

        mock.Setup(a => a.CanAdministerCompanyAsync(It.IsAny<int>()))
            .ReturnsAsync((int companyId) => access.CanAdminister(companyId));

        // Location rights follow the company that owns the location. Tests that care about the
        // difference set this up themselves.
        mock.Setup(a => a.CanAdministerLocationAsync(It.IsAny<int>())).ReturnsAsync(access.IsAdmin);

        // Running a venue's service settings is wider than administering its company, but every
        // caller who can do the latter can do the former, so the same answer serves here. Set up
        // explicitly rather than left to Moq's default of false, which would have quietly failed
        // every opening-hours and printer test for a reason unrelated to what it was testing.
        mock.Setup(a => a.CanManageVenueOperationsAsync(It.IsAny<int>())).ReturnsAsync(access.IsAdmin);

        return mock.Object;
    }
}
