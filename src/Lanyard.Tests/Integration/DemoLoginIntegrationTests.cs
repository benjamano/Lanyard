using System.Net;
using System.Text.RegularExpressions;
using Lanyard.Application.Services.Demo;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The homepage's one-click "Explore as ..." buttons, end to end: GET renders a self-submitting form
// with an antiforgery token, the POST signs the visitor in to the demo company.
[TestClass]
public class DemoLoginIntegrationTests
{
    private CustomWebApplicationFactory _baseFactory = null!;
    private WebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _baseFactory = new CustomWebApplicationFactory();
        _factory = _baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Demo:Enabled", "true");

            // The nightly reset uses ExecuteDelete, which EF InMemory can't run - the demo company is
            // seeded by hand below instead (DemoResetServiceTests covers the reset itself).
            b.ConfigureServices(services =>
            {
                ServiceDescriptor? reset = services.FirstOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(DemoResetHostedService));

                if (reset is not null)
                {
                    services.Remove(reset);
                }
            });
        });

        _factory.CreateClient();

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();

        Company demo = new() { Name = "Demo Co", IsDemo = true, IsActive = true };
        ctx.Companies.Add(demo);
        await ctx.SaveChangesAsync();

        Location location = new() { CompanyId = demo.Id, Name = "Demo Town", IsActive = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        UserProfile staff = new() { Id = DemoAccounts.StaffUserId, UserName = DemoAccounts.StaffUserId, FirstName = "Jordan", LastName = "Lee" };
        Assert.IsTrue((await userManager.CreateAsync(staff)).Succeeded);
        await userManager.AddToRoleAsync(staff, "Staff");

        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = staff.Id, LocationId = location.Id });
        await ctx.SaveChangesAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private HttpClient NoRedirects() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Dictionary<string, string> ReadHiddenFields(string html) =>
        Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\"")
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value), m => WebUtility.HtmlDecode(m.Groups[2].Value));

    [TestMethod]
    public async Task ExploreAsStaff_SignsTheVisitorInToTheDemoCompany()
    {
        HttpClient client = NoRedirects();

        string formHtml = await client.GetStringAsync("/api/auth/demo-login?role=staff");
        Dictionary<string, string> fields = ReadHiddenFields(formHtml);

        Assert.AreEqual("staff", fields["role"]);

        HttpResponseMessage login = await client.PostAsync("/api/auth/demo-login", new FormUrlEncodedContent(fields));

        Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
        Assert.AreEqual("/", login.Headers.Location?.OriginalString);

        string home = await client.GetStringAsync("/");

        StringAssert.Contains(home, "<title>Staff Home</title>");
        StringAssert.Contains(home, "exploring the Lanyard demo");
    }

    [TestMethod]
    public async Task DemoLogin_WithoutTheAntiforgeryToken_IsRejected()
    {
        HttpResponseMessage response = await NoRedirects().PostAsync("/api/auth/demo-login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["role"] = "staff" }));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task DemoLogin_ForAnUnknownRole_IsNotFound()
    {
        HttpResponseMessage response = await NoRedirects().GetAsync("/api/auth/demo-login?role=superuser");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task DemoLogin_WhenTheDemoIsOff_IsNotFound()
    {
        // The base factory has no Demo:Enabled.
        HttpResponseMessage response = await _baseFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .GetAsync("/api/auth/demo-login?role=staff");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
