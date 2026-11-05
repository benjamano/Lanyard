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

// /music used to need only a sign-in, so any member of staff could control the venue's music by
// typing the address. Check the role gate end to end rather than trusting the attribute.
[TestClass]
public class MusicPageAuthorizationIntegrationTests
{
    private const string Password = "Music-Page-Pw1!";
    private const string PageText = "music-page-title";

    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.CreateClient();

        await AddUserAsync("plainstaff", "Staff");
        await AddUserAsync("clipplayer", "Staff", "CanPlayAudioClips");
        await AddUserAsync("musiccontroller", "Staff", "CanControlMusic");
        await AddUserAsync("playlistmanager", "Staff", "CanManagePlaylists");
        await AddUserAsync("dutymanager", "Manager");
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

        HttpResponseMessage page = await client.GetAsync("/music");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);

        return await page.Content.ReadAsStringAsync();
    }

    [TestMethod]
    public async Task AnonymousUser_IsSentToLogin()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync("/music");

        Assert.IsTrue((int)response.StatusCode is >= 300 and < 400, $"Expected a redirect, got {(int)response.StatusCode}.");
        StringAssert.StartsWith(response.Headers.Location!.AbsolutePath, "/HandleLogin");
    }

    [TestMethod]
    [DataRow("plainstaff")]
    [DataRow("clipplayer")]
    public async Task StaffWithoutAMusicRole_AreRefused(string userName)
    {
        string html = await GetPageAsAsync(userName);

        Assert.IsFalse(html.Contains(PageText), $"The Music page rendered for {userName}.");
    }

    [TestMethod]
    [DataRow("musiccontroller")]
    [DataRow("playlistmanager")]
    [DataRow("dutymanager")]
    public async Task MusicRolesAndManagers_SeeThePage(string userName)
    {
        string html = await GetPageAsAsync(userName);

        StringAssert.Contains(html, PageText);
    }
}
