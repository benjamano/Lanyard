using System.Text.RegularExpressions;
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

    private const string PublicHost = "https://lanyard.benjaminmercer.co.uk";

    private HttpClient OnHost(string baseAddress) =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri(baseAddress) });

    // The homepage is the one page search engines may index - and only on the public host.
    [TestMethod]
    public async Task Homepage_OnThePublicHost_IsIndexableWithItsOwnTitleAndDescription()
    {
        HttpResponseMessage response = await OnHost(PublicHost).GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.IsFalse(response.Headers.Contains("X-Robots-Tag"), "The public homepage must not be marked noindex.");
        Assert.AreEqual(1, Regex.Matches(html, "<title>").Count, "Exactly one <title>, or crawlers may pick the generic one.");
        StringAssert.Contains(html, "<title>Lanyard - run your whole venue from one app</title>");
        Assert.AreEqual(1, Regex.Matches(html, "<meta name=\"description\"").Count);
        StringAssert.Contains(html, "<link rel=\"canonical\" href=\"https://lanyard.benjaminmercer.co.uk/\"");
        StringAssert.Contains(html, "<meta property=\"og:image\" content=\"https://lanyard.benjaminmercer.co.uk/logo.png\"");
    }

    [TestMethod]
    public async Task EverythingElse_StaysOutOfSearchResults()
    {
        // Another host (staging, Railway's own address) and any other page on the public host.
        HttpResponseMessage otherHost = await OnHost("https://staging.example.com").GetAsync("/");
        HttpResponseMessage login = await OnHost(PublicHost).GetAsync("/login");

        Assert.AreEqual("noindex, nofollow", otherHost.Headers.GetValues("X-Robots-Tag").Single());
        Assert.AreEqual("noindex, nofollow", login.Headers.GetValues("X-Robots-Tag").Single());
    }

    [TestMethod]
    public async Task RobotsTxt_AllowsOnlyTheHomepage()
    {
        string robots = await OnHost(PublicHost).GetStringAsync("/robots.txt");

        StringAssert.Contains(robots, "Allow: /$");
        StringAssert.Contains(robots, "Disallow: /");
        StringAssert.Contains(robots, "Sitemap: https://lanyard.benjaminmercer.co.uk/sitemap.xml");
        StringAssert.Contains(await OnHost(PublicHost).GetStringAsync("/sitemap.xml"), "<loc>https://lanyard.benjaminmercer.co.uk/</loc>");
    }
}
