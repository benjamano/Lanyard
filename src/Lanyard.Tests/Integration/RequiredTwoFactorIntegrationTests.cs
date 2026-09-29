using System.Net;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The required-2FA check lives in RouteAuthorizationGate, so a service test can't show that a
// page is actually swapped for the set-up screen. These render real pages through the pipeline.
[TestClass]
public class RequiredTwoFactorIntegrationTests
{
    private const string SetUpScreenHeading = "Set up two-factor authentication to continue";

    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new CustomWebApplicationFactory();

        // Forces host startup (and so DatabaseSeeder) before touching the seeded rows.
        _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private async Task RequireTwoFactorForSeedCompanyAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Company company = await ctx.Companies.SingleAsync(x => x.Id == ApplicationDbContext.SeedPlay2DayCompanyId);
        company.TwoFactorRequiredSince = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }

    private async Task EnableTwoFactorForSeedAdminAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();
        UserProfile admin = await userManager.FindByNameAsync("admin")
            ?? throw new InvalidOperationException("Seed admin user not found.");

        await userManager.SetTwoFactorEnabledAsync(admin, true);
    }

    private async Task<HttpClient> LoginAsSeedAdminAtIpswichAsync()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = "admin",
            Password = CustomWebApplicationFactory.SeedAdminPassword,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });

        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
        return client;
    }

    [TestMethod]
    public async Task CompanyRequiresTwoFactor_UserWithoutIt_GetsTheSetUpScreenInsteadOfThePage()
    {
        await RequireTwoFactorForSeedCompanyAsync();
        HttpClient client = await LoginAsSeedAdminAtIpswichAsync();

        string html = await client.GetStringAsync("/manage/users");

        StringAssert.Contains(html, SetUpScreenHeading);
    }

    // The home page is [AllowAnonymous] so signed-out visitors get the public homepage, and it's
    // where everyone lands after signing in - so it must be held too, not waved through as anonymous.
    [TestMethod]
    public async Task CompanyRequiresTwoFactor_UserWithoutIt_IsHeldOnTheAnonymousHomePageToo()
    {
        await RequireTwoFactorForSeedCompanyAsync();
        HttpClient client = await LoginAsSeedAdminAtIpswichAsync();

        string html = await client.GetStringAsync("/");

        StringAssert.Contains(html, SetUpScreenHeading);
    }

    [TestMethod]
    public async Task CompanyRequiresTwoFactor_PageMarkedAllowWithoutTwoFactor_IsNotHeld()
    {
        await RequireTwoFactorForSeedCompanyAsync();
        HttpClient client = await LoginAsSeedAdminAtIpswichAsync();

        string html = await client.GetStringAsync("/privacy");

        Assert.IsFalse(html.Contains(SetUpScreenHeading), "Pages like the privacy policy and sign-out must stay reachable.");
    }

    [TestMethod]
    public async Task CompanyRequiresTwoFactor_UserWithIt_GetsThePage()
    {
        await RequireTwoFactorForSeedCompanyAsync();
        HttpClient client = await LoginAsSeedAdminAtIpswichAsync();
        await EnableTwoFactorForSeedAdminAsync();

        string html = await client.GetStringAsync("/manage/users");

        Assert.IsFalse(html.Contains(SetUpScreenHeading), "A user who already has 2FA shouldn't be held on the set-up screen.");
        StringAssert.Contains(html, "User Management");
    }

    [TestMethod]
    public async Task CompanyDoesNotRequireTwoFactor_UserWithoutIt_GetsThePage()
    {
        HttpClient client = await LoginAsSeedAdminAtIpswichAsync();

        string html = await client.GetStringAsync("/manage/users");

        Assert.IsFalse(html.Contains(SetUpScreenHeading));
    }
}
