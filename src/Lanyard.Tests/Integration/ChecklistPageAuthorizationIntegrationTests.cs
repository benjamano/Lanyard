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

// The checklist setup pages run through the real pipeline: anonymous visitors go to sign-in,
// staff without the Manager role are refused, and a Manager gets the page.
[TestClass]
public class ChecklistPageAuthorizationIntegrationTests
{
    private const string Password = "Checklist-Pw1!";
    private const string PageText = "Routines this location runs";

    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.CreateClient();

        await AddUserAsync("checkliststaff", "Staff");
        await AddUserAsync("checklistmanager", "Staff", "Manager");
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

    private async Task<string> GetPageAsAsync(string userName, string path)
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = userName,
            Password = Password,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });
        Assert.IsTrue(login.IsSuccessStatusCode, $"Login failed: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        HttpResponseMessage page = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);

        return await page.Content.ReadAsStringAsync();
    }

    [TestMethod]
    [DataRow("/manage/checklists")]
    [DataRow("/manage/checklists/new")]
    [DataRow("/help/manage-checklists")]
    public async Task AnonymousUser_IsSentToLogin(string path)
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync(path);

        Assert.IsTrue((int)response.StatusCode is >= 300 and < 400, $"Expected a redirect for {path}, got {(int)response.StatusCode}.");
        StringAssert.StartsWith(response.Headers.Location!.AbsolutePath, "/HandleLogin");
    }

    [TestMethod]
    public async Task StaffWithoutTheManagerRole_AreRefused()
    {
        string html = await GetPageAsAsync("checkliststaff", "/manage/checklists");

        Assert.IsFalse(html.Contains(PageText), "The checklist setup page rendered for a plain staff member.");
    }

    [TestMethod]
    public async Task Manager_SeesThePage()
    {
        string html = await GetPageAsAsync("checklistmanager", "/manage/checklists");

        StringAssert.Contains(html, PageText);
    }
}
