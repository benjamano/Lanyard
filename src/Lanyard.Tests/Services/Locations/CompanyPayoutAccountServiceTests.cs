using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Locations;

/// <summary>
/// Changing where a company's takings land.
///
/// This is the most damaging field in the app: repointing it sends every future payment to
/// somebody else's bank account. These assert it takes an admin and a fresh second factor, and
/// that neither can be skipped.
/// </summary>
[TestClass]
public class CompanyPayoutAccountServiceTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static Mock<ISecurityService> SecurityAccepting(bool codeIsValid)
    {
        Mock<ISecurityService> mock = new();
        mock.Setup(s => s.VerifySecondFactorAsync(It.IsAny<string>()))
            .ReturnsAsync(codeIsValid ? Result<bool>.Ok(true) : Result<bool>.Fail("Invalid or expired code."));

        return mock;
    }

    private static CompanyPayoutAccountService GetService(
        DbContextOptions<ApplicationDbContext> options,
        ICompanyAccessService access,
        Mock<ISecurityService> security)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        return new CompanyPayoutAccountService(
            factoryMock.Object, access, security.Object,
            new Mock<ILogger<CompanyPayoutAccountService>>().Object);
    }

    private static async Task<int> SeedCompanyAsync(
        DbContextOptions<ApplicationDbContext> options, string? stripeAccountId = null)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = "Play2Day", IsActive = true, StripeAccountId = stripeAccountId };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        return company.Id;
    }

    private static async Task<string?> AccountOf(DbContextOptions<ApplicationDbContext> options, int companyId)
    {
        await using ApplicationDbContext ctx = new(options);

        return (await ctx.Companies.FirstAsync(c => c.Id == companyId)).StripeAccountId;
    }

    [TestMethod]
    public async Task Admin_WithAValidCode_CanChangeTheAccount()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);

        Result<bool> result = await GetService(options, CompanyAccessMocks.Admin(), SecurityAccepting(true))
            .SetStripeAccountIdAsync(companyId, "acct_new", "123456");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("acct_new", await AccountOf(options, companyId));
    }

    [TestMethod]
    public async Task Admin_WithoutAValidCode_CannotChangeTheAccount()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options, "acct_original");

        Result<bool> result = await GetService(options, CompanyAccessMocks.Admin(), SecurityAccepting(false))
            .SetStripeAccountIdAsync(companyId, "acct_attacker", "000000");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("acct_original", await AccountOf(options, companyId));
    }

    /// <summary>
    /// A manager runs their own company's branding and wording, but not where its money goes.
    /// </summary>
    [TestMethod]
    public async Task Manager_CannotChangeTheAccountEvenForTheirOwnCompanyWithAValidCode()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options, "acct_original");

        Mock<ISecurityService> security = SecurityAccepting(true);

        Result<bool> result = await GetService(options, CompanyAccessMocks.ManagerOf(companyId), security)
            .SetStripeAccountIdAsync(companyId, "acct_attacker", "123456");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("acct_original", await AccountOf(options, companyId));

        // Refused on the role alone; the code was never even checked.
        security.Verify(s => s.VerifySecondFactorAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Re-saving the same value is not a change, so it must not demand a code. Otherwise any
    /// unrelated save on the page would ask for one.
    /// </summary>
    [TestMethod]
    public async Task SettingTheSameAccountAgainDoesNotRequireACode()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options, "acct_same");

        Mock<ISecurityService> security = SecurityAccepting(false);

        Result<bool> result = await GetService(options, CompanyAccessMocks.Admin(), security)
            .SetStripeAccountIdAsync(companyId, "acct_same", string.Empty);

        Assert.IsTrue(result.IsSuccess, result.Error);
        security.Verify(s => s.VerifySecondFactorAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Clearing it stops the venue taking orders, which is as consequential as repointing it.
    /// </summary>
    [TestMethod]
    public async Task ClearingTheAccountStillRequiresACode()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options, "acct_original");

        Result<bool> refused = await GetService(options, CompanyAccessMocks.Admin(), SecurityAccepting(false))
            .SetStripeAccountIdAsync(companyId, null, "000000");

        Assert.IsFalse(refused.IsSuccess);
        Assert.AreEqual("acct_original", await AccountOf(options, companyId));

        Result<bool> allowed = await GetService(options, CompanyAccessMocks.Admin(), SecurityAccepting(true))
            .SetStripeAccountIdAsync(companyId, null, "123456");

        Assert.IsTrue(allowed.IsSuccess, allowed.Error);
        Assert.IsNull(await AccountOf(options, companyId));
    }

    [TestMethod]
    public async Task RejectsSomethingThatIsNotAStripeAccountId()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options);

        Result<bool> result = await GetService(options, CompanyAccessMocks.Admin(), SecurityAccepting(true))
            .SetStripeAccountIdAsync(companyId, "sk_live_totally_wrong", "123456");

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(await AccountOf(options, companyId));
    }

    /// <summary>
    /// The ordinary company save must not be a way around any of the above.
    /// </summary>
    [TestMethod]
    public async Task SaveCompanyAsync_LeavesThePayoutAccountAlone()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        int companyId = await SeedCompanyAsync(options, "acct_original");

        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        CompanyLocationService companies = new(factoryMock.Object, CompanyAccessMocks.Admin());

        await using (ApplicationDbContext read = new(options))
        {
            Company company = await read.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId);
            company.StripeAccountId = "acct_attacker";
            company.ThemeColorHex = "#123456";

            Assert.IsTrue((await companies.SaveCompanyAsync(company)).IsSuccess);
        }

        // The colour changed; the payout account did not.
        await using ApplicationDbContext check = new(options);
        Company saved = await check.Companies.FirstAsync(c => c.Id == companyId);
        Assert.AreEqual("#123456", saved.ThemeColorHex);
        Assert.AreEqual("acct_original", saved.StripeAccountId);
    }
}
