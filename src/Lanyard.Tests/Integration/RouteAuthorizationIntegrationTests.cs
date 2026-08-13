using System.Net;
using System.Net.Http.Json;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// These are integration tests, not the service-level unit tests elsewhere in this project - they
// boot the real ASP.NET Core pipeline (auth middleware, routing, RouteAuthorizationGate) rather
// than mocking it away. See AGENTS.md's "When a service-level unit test isn't enough" for why this
// class of test exists: RouteAuthorizationGate's default-deny behavior is exactly the kind of bug
// (Routes.razor previously used RouteView instead of AuthorizeRouteView, leaving every page
// reachable anonymously) that every service-level unit test still passed while it was broken.
[TestClass]
public class RouteAuthorizationIntegrationTests
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

    [TestMethod]
    public async Task AnonymousUser_AccessingAuthorizePage_RedirectsToLogin()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync("/manage/dashboards");

        Assert.IsTrue((int)response.StatusCode is >= 300 and < 400, $"Expected a redirect, got {(int)response.StatusCode}.");
        Assert.IsNotNull(response.Headers.Location);
        StringAssert.StartsWith(response.Headers.Location!.AbsolutePath, "/HandleLogin");
    }

    [TestMethod]
    public async Task AnonymousUser_AccessingAllowAnonymousPage_ReturnsOk()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/not-found");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AnonymousUser_DeletingFileViaAdminOnlyEndpoint_IsRejected()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.DeleteAsync($"/api/files/{Guid.NewGuid()}");

        Assert.IsFalse(response.IsSuccessStatusCode, $"Expected the admin-only endpoint to reject an anonymous caller, got {(int)response.StatusCode}.");
    }

    [TestMethod]
    public async Task AuthenticatedAdmin_AccessingAuthorizePage_ReturnsOk()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = "admin",
            Password = CustomWebApplicationFactory.SeedAdminPassword
        });

        Assert.IsTrue(loginResponse.IsSuccessStatusCode,
            $"Seed admin login failed: {(int)loginResponse.StatusCode} {await loginResponse.Content.ReadAsStringAsync()}");

        HttpResponseMessage pageResponse = await client.GetAsync("/manage/dashboards");

        Assert.AreEqual(HttpStatusCode.OK, pageResponse.StatusCode);
    }
}
