using System.Net;
using System.Net.Http.Json;
using Lanyard.Application.Services.Authentication;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The per-user date format is delivered by the standard ASP.NET culture cookie, written at
// login and read by the request-localization middleware. These pin the two halves together.
[TestClass]
public class CultureCookieIntegrationTests
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
    public async Task Login_OnSuccess_SetsTheCultureCookie()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = "admin",
            Password = CustomWebApplicationFactory.SeedAdminPassword,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });

        Assert.IsTrue(response.IsSuccessStatusCode, $"Seed admin login failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        string? cultureCookie = response.Headers
            .GetValues("Set-Cookie")
            .FirstOrDefault(c => c.StartsWith(UserCultureCookie.CookieName + "=", StringComparison.Ordinal));

        Assert.IsNotNull(cultureCookie, "Login must write the culture cookie the localization middleware reads.");
        StringAssert.Contains(Uri.UnescapeDataString(cultureCookie), "c=en-GB|uic=en-GB");
    }

    [TestMethod]
    public void MakeCookieValue_FallsBackToDefaultForUnknownOrMissingCultures()
    {
        StringAssert.Contains(UserCultureCookie.MakeCookieValue(null), "c=en-GB|uic=en-GB");
        StringAssert.Contains(UserCultureCookie.MakeCookieValue("en-US"), "c=en-US|uic=en-US");
        StringAssert.Contains(UserCultureCookie.MakeCookieValue("not-a-culture-xx"), "c=en-GB|uic=en-GB");
    }
}
