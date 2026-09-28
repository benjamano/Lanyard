using System.Net;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// "/" is the staff dashboard (Home.razor, [Authorize]) for signed-in users and the public
// homepage for everyone else - RouteAuthorizationGate swaps one for the other.
[TestClass]
public class PublicHomepageIntegrationTests
{
    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new CustomWebApplicationFactory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private static HttpClient NoRedirects(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [TestMethod]
    public async Task AnonymousVisitor_GetsThePublicHomepage_NotALoginRedirect()
    {
        HttpResponseMessage response = await NoRedirects(_factory).GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "Run your whole venue from one app.");
        StringAssert.Contains(html, "href=\"/login\"");
    }

    [TestMethod]
    public async Task DemoSection_IsHiddenUnlessTheDemoIsEnabled()
    {
        string html = await NoRedirects(_factory).GetStringAsync("/");

        Assert.IsFalse(html.Contains("/api/auth/demo-login"), "No demo buttons should show while Demo:Enabled is off.");

        using WebApplicationFactory<Program> demoFactory = _factory.WithWebHostBuilder(b => b.UseSetting("Demo:Enabled", "true"));
        string demoHtml = await NoRedirects(demoFactory).GetStringAsync("/");

        StringAssert.Contains(demoHtml, "/api/auth/demo-login?role=admin");
        StringAssert.Contains(demoHtml, "/api/auth/demo-login?role=manager");
        StringAssert.Contains(demoHtml, "/api/auth/demo-login?role=staff");
    }

    [TestMethod]
    public async Task SignedInUser_StillGetsTheStaffHome()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = "admin",
            Password = CustomWebApplicationFactory.SeedAdminPassword,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });
        Assert.IsTrue(login.IsSuccessStatusCode);

        string html = await client.GetStringAsync("/");

        Assert.IsFalse(html.Contains("Run your whole venue from one app."));
        StringAssert.Contains(html, "<title>Staff Home</title>");
    }
}
