using System.Net;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The Audio Clips page is the one place a non-manager role (CanPlayAudioClips) gets into the
// music side of the app, so check the role gate end to end rather than trusting the attribute.
[TestClass]
public class AudioClipPageAuthorizationIntegrationTests
{
    private const string Password = "Audio-Clip-Pw1!";
    private const string PageText = "Short recordings to play over the kiosk music";

    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.CreateClient();

        await AddUserAsync("plainstaff", "Staff");
        await AddUserAsync("clipplayer", "Staff", "CanPlayAudioClips");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private async Task AddUserAsync(string userName, params string[] roles)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();

        UserProfile user = new() { UserName = userName, Email = $"{userName}@example.com", FirstName = userName, LastName = "Test", EmailConfirmed = true };
        IdentityResult created = await userManager.CreateAsync(user, Password);
        Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));

        foreach (string role in roles)
        {
            await userManager.AddToRoleAsync(user, role);
        }

        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = user.Id, LocationId = ApplicationDbContext.SeedIpswichLocationId });
        await ctx.SaveChangesAsync();
    }

    private async Task<string> GetPageAsAsync(string userName)
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = userName,
            Password = Password,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });
        Assert.IsTrue(login.IsSuccessStatusCode, $"Login failed: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        HttpResponseMessage page = await client.GetAsync("/manage/audio-clips");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);

        return await page.Content.ReadAsStringAsync();
    }

    [TestMethod]
    public async Task AnonymousUser_IsSentToLogin()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync("/manage/audio-clips");

        Assert.IsTrue((int)response.StatusCode is >= 300 and < 400, $"Expected a redirect, got {(int)response.StatusCode}.");
        StringAssert.StartsWith(response.Headers.Location!.AbsolutePath, "/HandleLogin");
    }

    [TestMethod]
    public async Task StaffWithoutTheRole_AreRefused()
    {
        string html = await GetPageAsAsync("plainstaff");

        Assert.IsFalse(html.Contains(PageText), "The page rendered for a user without the role.");
    }

    [TestMethod]
    public async Task StaffWithCanPlayAudioClips_SeeThePage()
    {
        string html = await GetPageAsAsync("clipplayer");

        StringAssert.Contains(html, PageText);
    }
}
