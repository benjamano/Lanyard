using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// End to end through the real pipeline: login issues the company claim, the scoped
// TenantContext reads it back on the next request, and the DbContext filter uses it.
[TestClass]
public class CompanyTenancyIntegrationTests
{
    private const string OtherAdminPassword = "Other-Company-Admin-Pw1!";

    private CustomWebApplicationFactory _factory = null!;
    private int _otherCompanyLocationId;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();

        // Forces host startup (and so DatabaseSeeder) before seeding our own rows.
        _factory.CreateClient();

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();

        Company otherCompany = new() { Name = "Other Co", IsActive = true };
        ctx.Companies.Add(otherCompany);
        await ctx.SaveChangesAsync();

        Location otherLocation = new() { CompanyId = otherCompany.Id, Name = "Elsewhere", IsActive = true };
        ctx.Locations.Add(otherLocation);

        // One file per company; the scope has no signed-in user, so these writes are "system".
        ctx.FileMetadata.AddRange(
            new FileMetadata { Id = Guid.NewGuid(), FileName = "play2day.pdf", FilePath = "a", CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId },
            new FileMetadata { Id = Guid.NewGuid(), FileName = "other.pdf", FilePath = "b", CompanyId = otherCompany.Id });
        await ctx.SaveChangesAsync();
        _otherCompanyLocationId = otherLocation.Id;

        UserProfile otherAdmin = new() { UserName = "otheradmin", Email = "otheradmin@example.com", FirstName = "Other", LastName = "Admin", EmailConfirmed = true };
        IdentityResult created = await userManager.CreateAsync(otherAdmin, OtherAdminPassword);
        Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(otherAdmin, "Admin");

        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = otherAdmin.Id, LocationId = otherLocation.Id });
        await ctx.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password, int locationId) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginDto { Username = username, Password = password, LocationId = locationId });

    [TestMethod]
    public async Task CompanyAdmin_CannotSignInToAnotherCompanysLocation()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await LoginAsync(client, "otheradmin", OtherAdminPassword, ApplicationDbContext.SeedIpswichLocationId);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "You do not have access to the selected location.");
    }

    [TestMethod]
    public async Task PlatformAdmin_CanSignInToAnyCompanysLocation()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await LoginAsync(client, "admin", CustomWebApplicationFactory.SeedAdminPassword, _otherCompanyLocationId);

        Assert.IsTrue(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    [TestMethod]
    public async Task SignedInUser_OnlyGetsTheirOwnCompanysFiles()
    {
        HttpClient otherClient = _factory.CreateClient();
        HttpResponseMessage otherLogin = await LoginAsync(otherClient, "otheradmin", OtherAdminPassword, _otherCompanyLocationId);
        Assert.IsTrue(otherLogin.IsSuccessStatusCode, $"{(int)otherLogin.StatusCode} {await otherLogin.Content.ReadAsStringAsync()}");

        HttpClient play2DayClient = _factory.CreateClient();
        HttpResponseMessage play2DayLogin = await LoginAsync(play2DayClient, "admin", CustomWebApplicationFactory.SeedAdminPassword, ApplicationDbContext.SeedIpswichLocationId);
        Assert.IsTrue(play2DayLogin.IsSuccessStatusCode);

        CollectionAssert.AreEqual(new[] { "other.pdf" }, await ListFileNamesAsync(otherClient));
        CollectionAssert.AreEqual(new[] { "play2day.pdf" }, await ListFileNamesAsync(play2DayClient));
    }

    private static async Task<string[]> ListFileNamesAsync(HttpClient client)
    {
        // The endpoint returns the whole Result<T> envelope, not just the list.
        using JsonDocument json = JsonDocument.Parse(await client.GetStringAsync("/api/files/list"));

        return json.RootElement.GetProperty("data").EnumerateArray()
            .Select(x => x.GetProperty("fileName").GetString()!)
            .ToArray();
    }
}
